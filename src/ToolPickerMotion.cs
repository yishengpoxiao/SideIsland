using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;

namespace CodexIsland
{
    // The same live-height engine serves the picker and settings sections.
    // Picker headers remain a compact single line; section headings may wrap.
    internal class SmoothToolPicker : Expander
    {
        internal const int MotionMilliseconds = 160;
        private Border viewport;
        private ContentPresenter presenter;
        private ToggleButton header;
        private RotateTransform arrow;
        private FrameworkElement observedBody;
        private Stopwatch motionWatch;
        private double fromHeight, targetHeight, fromOpacity, fromAngle;
        private bool measuring, rendering;
        private ScrollViewer scroll;
        private double scrollOffset, scrollProgress;
        internal Func<bool> MotionAllowed;
        internal bool IsMotionActive { get { return rendering; } }
        internal double CurrentHeight { get { return viewport == null || viewport.Visibility == Visibility.Collapsed ? 0 : Double.IsNaN(viewport.Height) ? viewport.ActualHeight : viewport.Height; } }
        internal double TargetHeight { get { return targetHeight; } }

        internal SmoothToolPicker() : this(false) { }

        protected SmoothToolPicker(bool wrapHeader)
        {
            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetBinding(TextBlock.TextProperty, new Binding());
            if (wrapHeader) text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
            else text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            text.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            HeaderTemplate = new DataTemplate { VisualTree = text };
            SetBinding(ToolTipProperty, new Binding("Header") { Source = this });
            string template = @"<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='{x:Type Expander}'>
<DockPanel>
 <ToggleButton x:Name='PART_Header' DockPanel.Dock='Top' Height='32' IsChecked='{Binding IsExpanded, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}' Foreground='{TemplateBinding Foreground}' Background='Transparent' FocusVisualStyle='{x:Null}' HorizontalContentAlignment='Stretch'>
  <ToggleButton.Template><ControlTemplate TargetType='{x:Type ToggleButton}'>
   <Border x:Name='Frame' Background='{TemplateBinding Background}' CornerRadius='8' Padding='2,5'><Grid><Grid.ColumnDefinitions><ColumnDefinition Width='16'/><ColumnDefinition Width='*'/></Grid.ColumnDefinitions>
    <Path x:Name='Arrow' Data='M 0,0 L 4,4 L 0,8' Stroke='{TemplateBinding Foreground}' StrokeThickness='1.5' StrokeStartLineCap='Round' StrokeEndLineCap='Round' Width='5' Height='8' HorizontalAlignment='Left' VerticalAlignment='Center' RenderTransformOrigin='.5,.5'><Path.RenderTransform><RotateTransform/></Path.RenderTransform></Path>
    <ContentPresenter Grid.Column='1' Content='{Binding Header, RelativeSource={RelativeSource AncestorType={x:Type Expander}}}' ContentTemplate='{Binding HeaderTemplate, RelativeSource={RelativeSource AncestorType={x:Type Expander}}}' VerticalAlignment='Center'/>
   </Grid></Border>
   <ControlTemplate.Triggers><Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Frame' Property='Background' Value='{DynamicResource Island.Hover}'/></Trigger><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Frame' Property='Background' Value='{DynamicResource Island.Hover}'/></Trigger></ControlTemplate.Triggers>
  </ControlTemplate></ToggleButton.Template>
 </ToggleButton>
 <Border x:Name='PART_PickerViewport' ClipToBounds='True' Visibility='Collapsed'><ContentPresenter x:Name='PART_PickerContent' ContentSource='Content' VerticalAlignment='Top'/></Border>
</DockPanel></ControlTemplate>";
            if (wrapHeader) template = template.Replace("Height='32'", "MinHeight='32'");
            Template = (ControlTemplate)XamlReader.Parse(template);
            SizeChanged += (s, e) => { if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > .5) RefreshTarget(); };
            Unloaded += (s, e) => SettleMotion();
        }

        public override void OnApplyTemplate()
        {
            if (observedBody != null) observedBody.SizeChanged -= OnBodySizeChanged;
            SettleMotion(); base.OnApplyTemplate();
            viewport = GetTemplateChild("PART_PickerViewport") as Border;
            presenter = GetTemplateChild("PART_PickerContent") as ContentPresenter;
            header = GetTemplateChild("PART_Header") as ToggleButton;
            arrow = null;
            if (header != null)
            {
                header.ApplyTemplate();
                var path = header.Template.FindName("Arrow", header) as System.Windows.Shapes.Path;
                if (path != null)
                {
                    // Template Freezables can be frozen and shared. Motion owns a
                    // writable transform for this instantiated header only.
                    var templateArrow = path.RenderTransform as RotateTransform;
                    arrow = templateArrow == null ? new RotateTransform() : templateArrow.CloneCurrentValue();
                    path.RenderTransform = arrow;
                }
            }
            observedBody = Content as FrameworkElement;
            if (observedBody != null) observedBody.SizeChanged += OnBodySizeChanged;
            Commit();
        }
        private void OnBodySizeChanged(object sender, SizeChangedEventArgs e) { if (!measuring && IsExpanded) RefreshTarget(); }
        protected override void OnExpanded() { base.OnExpanded(); Retarget(); }
        protected override void OnCollapsed() { base.OnCollapsed(); Retarget(); }

        internal void RefreshTarget()
        {
            if (viewport == null || measuring || !IsExpanded) return;
            Retarget();
        }

        private bool CanMove { get { return IsLoaded && IsVisible && SystemParameters.ClientAreaAnimation && (MotionAllowed == null || MotionAllowed()); } }
        private double NaturalHeight()
        {
            if (presenter == null) return 0;
            double width = viewport.ActualWidth > 0 ? viewport.ActualWidth : Math.Max(1, ActualWidth);
            measuring = true;
            try
            {
                var body = Content as FrameworkElement;
                if (body != null) body.InvalidateMeasure();
                presenter.InvalidateMeasure(); presenter.Measure(new Size(width, Double.PositiveInfinity));
                return Math.Max(0, presenter.DesiredSize.Height);
            }
            finally { measuring = false; }
        }

        private void Retarget()
        {
            if (viewport == null) return;
            double current = CurrentHeight, target = IsExpanded ? NaturalHeight() : 0;
            if (rendering && Math.Abs(target - targetHeight) < .5) return;
            if (!CanMove || Math.Abs(current - target) < .5) { SettleMotion(); return; }
            Detach(); fromHeight = current; targetHeight = target; fromOpacity = viewport.Visibility == Visibility.Collapsed ? 0 : viewport.Opacity;
            fromAngle = arrow == null ? 0 : arrow.Angle;
            viewport.Visibility = Visibility.Visible; viewport.Height = current; viewport.Opacity = fromOpacity;
            viewport.IsHitTestVisible = IsExpanded;
            if (!IsExpanded && presenter.IsKeyboardFocusWithin && header != null && header.IsVisible) Keyboard.Focus(header);
            motionWatch = null; rendering = true; CompositionTarget.Rendering += OnRendering;
        }
        private double Progress { get { return motionWatch == null ? 0 : Math.Min(1, motionWatch.Elapsed.TotalMilliseconds / MotionMilliseconds); } }
        private void OnRendering(object sender, EventArgs e)
        {
            if (!rendering || !CanMove) { SettleMotion(); return; }
            if (motionWatch == null) motionWatch = Stopwatch.StartNew();
            double progress = Progress, ease = .5 - Math.Cos(Math.PI * progress) / 2;
            viewport.Height = fromHeight + (targetHeight - fromHeight) * ease;
            viewport.Opacity = fromOpacity + ((IsExpanded ? 1 : 0) - fromOpacity) * ease;
            if (arrow != null) arrow.Angle = fromAngle + ((IsExpanded ? 90 : 0) - fromAngle) * ease;
            if (scroll != null)
            {
                double remaining = Math.Max(.001, 1 - scrollProgress);
                double normalized = Math.Max(0, Math.Min(1, (progress - scrollProgress) / remaining));
                scroll.ScrollToVerticalOffset(scrollOffset * (.5 + Math.Cos(Math.PI * normalized) / 2));
            }
            if (progress >= 1) SettleMotion();
        }
        internal void SmoothScrollToTop(ScrollViewer value)
        {
            if (!rendering || IsExpanded) { value.ScrollToTop(); return; }
            scroll = value; scrollOffset = value.VerticalOffset; scrollProgress = Progress;
        }
        private void Detach()
        {
            CompositionTarget.Rendering -= OnRendering; rendering = false; motionWatch = null; scroll = null;
        }
        internal void SettleMotion()
        {
            if (scroll != null && !IsExpanded) scroll.ScrollToTop();
            Detach(); Commit();
        }
        private void Commit()
        {
            if (viewport == null) return;
            viewport.Height = Double.NaN; viewport.Opacity = 1; viewport.IsHitTestVisible = IsExpanded;
            viewport.Visibility = IsExpanded ? Visibility.Visible : Visibility.Collapsed;
            targetHeight = IsExpanded ? NaturalHeight() : 0;
            if (arrow != null) arrow.Angle = IsExpanded ? 90 : 0;
        }
    }

    internal sealed class SmoothSettingsSection : SmoothToolPicker
    {
        internal SmoothSettingsSection() : base(true) { }

        protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            // Translating a section title can change its wrapped header/body
            // measurement. Finish its local motion before that new layout.
            if (e.Property == HeaderProperty) SettleMotion();
        }
    }

    internal partial class IslandWindow
    {
        private void SettleToolPickerMotion() { var picker = toolPicker as SmoothToolPicker; if (picker != null) picker.SettleMotion(); }
    }
}
