using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodexIsland
{
    internal partial class IslandWindow
    {
        internal const int NotificationPeekTransitionMilliseconds = 150;
        private StackPanel peekContent;
        private TranslateTransform peekTranslation;
        private bool peekTransitionActive;
        private Stopwatch peekTransitionWatch;
        private int peekTransitionVersion;
        private double peekTransitionFromOpacity, peekTransitionFromY;
        private string peekRenderedSignature;

        private bool CanAnimateNotificationPeek
        {
            get
            {
                return peeking && !expanded && IsLoaded && IsVisible && Opacity > 0 &&
                    !shuttingDown && !dragging && !resizing && !visualTransitionActive && !pageTransitionActive &&
                    SystemParameters.ClientAreaAnimation && peekPanel != null && peekPanel.IsVisible &&
                    peekContent != null && peekContent.IsVisible;
            }
        }

        private void BeginNotificationPeekTransition(double opacity, double offset)
        {
            StopNotificationPeekTransition();
            if (!CanAnimateNotificationPeek) return;
            peekTransitionFromOpacity = Math.Max(.35, Math.Min(1, opacity));
            peekTransitionFromY = Math.Max(-2, Math.Min(2, offset));
            peekContent.Opacity = peekTransitionFromOpacity; peekTranslation.Y = peekTransitionFromY;
            peekTransitionActive = true; peekTransitionWatch = null; ++peekTransitionVersion;
            CompositionTarget.Rendering += OnNotificationPeekRendering;
        }

        private void OnNotificationPeekRendering(object sender, EventArgs e)
        {
            if (!peekTransitionActive || !CanAnimateNotificationPeek)
            { StopNotificationPeekTransition(); return; }
            if (peekTransitionWatch == null) peekTransitionWatch = Stopwatch.StartNew();
            double progress = Math.Min(1, peekTransitionWatch.Elapsed.TotalMilliseconds / NotificationPeekTransitionMilliseconds);
            double ease = .5 - Math.Cos(Math.PI * progress) / 2;
            peekContent.Opacity = peekTransitionFromOpacity + (1 - peekTransitionFromOpacity) * ease;
            peekTranslation.Y = peekTransitionFromY * (1 - ease);
            if (progress >= 1) StopNotificationPeekTransition();
        }

        private void StopNotificationPeekTransition()
        {
            CompositionTarget.Rendering -= OnNotificationPeekRendering;
            bool active = peekTransitionActive || peekTransitionWatch != null;
            peekTransitionActive = false; peekTransitionWatch = null;
            if (peekContent != null) peekContent.Opacity = 1;
            if (peekTranslation != null) peekTranslation.Y = 0;
            if (active) ++peekTransitionVersion;
        }

        private void ClearNotificationPeekContent()
        {
            StopNotificationPeekTransition();
            peekMessage = null; peekAppId = ""; peekRenderedSignature = null;
            foreach (var text in new[] { peekSource, peekKind, peekHint, peekTitle, peekBody })
                if (text != null) { text.Text = ""; text.ToolTip = null; }
        }

        private string NotificationPeekSignature(IslandNotification message)
        {
            string title = settings.HideNotificationBody ? UiLocalization.Localize("收到一条新通知") : message.Title;
            string body = settings.HideNotificationBody ? UiLocalization.Localize("消息内容已隐藏") : message.Body;
            return PeekPart(message.Id) + PeekPart(message.AppId) + PeekPart(message.CreatedAt.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)) + PeekPart(message.AppName) +
                PeekPart(title) + PeekPart(body) + PeekPart(message.IsDemo ? "demo" : "normal") +
                PeekPart(UiLocalization.Language) + PeekPart(settings.HideNotificationBody ? "private" : "full");
        }

        private static string PeekPart(string value) { value = value ?? ""; return value.Length + ":" + value; }

        private static IslandNotification CopyPeekNotification(IslandNotification message)
        {
            return new IslandNotification { Id = message.Id, AppId = message.AppId, AppName = message.AppName,
                Title = message.Title, Body = message.Body, CreatedAt = message.CreatedAt, IsRead = message.IsRead,
                IsDemo = message.IsDemo, RepeatCount = message.RepeatCount, PresentationPriority = message.PresentationPriority };
        }
    }
}
