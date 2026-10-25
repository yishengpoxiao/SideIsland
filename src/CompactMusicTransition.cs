using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace CodexIsland
{
    internal sealed partial class CompactMusicVisual
    {
        internal const int ContentTransitionMilliseconds = 150;
        private sealed class ContentMotion
        {
            internal bool Active;
            internal double Opacity = 1, Y, FromOpacity = 1, FromY;
            internal int Version;
            internal Stopwatch Watch;
        }
        private readonly ContentMotion contentMotion = new ContentMotion(), lyricMotion = new ContentMotion();
        private bool contentInitialized, transitionRenderingAttached;
        private string trackIdentity = "";
        private int contentRevision;

        internal int ContentRevision { get { return contentRevision; } }
        internal bool ContentTransitionActive { get { return contentMotion.Active; } }
        internal bool LyricTransitionActive { get { return lyricMotion.Active; } }
        internal double ContentTransitionOpacity { get { return contentMotion.Opacity; } }
        internal double ContentTransitionY { get { return contentMotion.Y; } }
        internal double LyricTransitionOpacity { get { return lyricMotion.Opacity; } }
        internal double LyricTransitionY { get { return lyricMotion.Y; } }
        internal int ContentTransitionVersion { get { return contentMotion.Version; } }
        internal int LyricTransitionVersion { get { return lyricMotion.Version; } }
        internal Stopwatch ContentTransitionWatch { get { return contentMotion.Watch; } }
        internal Stopwatch LyricTransitionWatch { get { return lyricMotion.Watch; } }
        internal bool TransitionRenderingAttached { get { return transitionRenderingAttached; } }

        private bool CanAnimateContent
        {
            get { return IsLoaded && IsVisible && Opacity > 0 && SystemParameters.ClientAreaAnimation; }
        }

        private void UpdateContentTransition(bool changed, bool changedLyric, bool animate)
        {
            if (!animate || !CanAnimateContent) { SettleContentTransition(); return; }
            if (changed) BeginMotion(contentMotion);
            if (changedLyric) BeginMotion(lyricMotion);
            if ((contentMotion.Active || lyricMotion.Active) && !transitionRenderingAttached)
            {
                transitionRenderingAttached = true;
                CompositionTarget.Rendering += OnContentTransitionRendering;
            }
        }

        private static void BeginMotion(ContentMotion motion)
        {
            // A rapid replacement starts at the pixels already drawn. There is
            // no outgoing artwork, metadata, lyric document or bitmap to retain.
            motion.FromOpacity = motion.Active ? motion.Opacity : .35;
            // Compact title/lyric slots can be exactly one glyph line tall.
            // Fade in place so large fonts and rotated text keep the same clip
            // geometry as their settled drawing, without trimming descenders.
            motion.FromY = 0;
            motion.Opacity = motion.FromOpacity; motion.Y = motion.FromY;
            motion.Active = true; motion.Watch = null; ++motion.Version;
        }

        private void OnContentTransitionRendering(object sender, EventArgs args)
        {
            if (!CanAnimateContent) { SettleContentTransition(); return; }
            AdvanceMotion(contentMotion); AdvanceMotion(lyricMotion);
            InvalidateVisual();
            if (!contentMotion.Active && !lyricMotion.Active) DetachContentRendering();
        }

        private static void AdvanceMotion(ContentMotion motion)
        {
            if (!motion.Active) return;
            if (motion.Watch == null) motion.Watch = Stopwatch.StartNew();
            double progress = Math.Min(1, motion.Watch.Elapsed.TotalMilliseconds / ContentTransitionMilliseconds);
            double ease = .5 - Math.Cos(Math.PI * progress) / 2;
            motion.Opacity = motion.FromOpacity + (1 - motion.FromOpacity) * ease;
            motion.Y = motion.FromY * (1 - ease);
            if (progress >= 1) SettleMotion(motion);
        }

        internal void SettleContentTransition()
        {
            bool changed = contentMotion.Active || lyricMotion.Active || contentMotion.Opacity != 1 || lyricMotion.Opacity != 1 || contentMotion.Y != 0 || lyricMotion.Y != 0;
            SettleMotion(contentMotion); SettleMotion(lyricMotion); DetachContentRendering();
            if (changed) InvalidateVisual();
        }

        private static void SettleMotion(ContentMotion motion)
        {
            if (motion.Active || motion.Watch != null) ++motion.Version;
            motion.Active = false; motion.Watch = null;
            motion.Opacity = motion.FromOpacity = 1; motion.Y = motion.FromY = 0;
        }

        private void DetachContentRendering()
        {
            if (!transitionRenderingAttached) return;
            CompositionTarget.Rendering -= OnContentTransitionRendering;
            transitionRenderingAttached = false;
        }

        private static void BeginContentDrawing(DrawingContext drawing, Rect bounds, ContentMotion motion, bool translate = true)
        {
            // Clips are fixed in the card's coordinates; the animated text can
            // never enter its transport buttons or progress track.
            drawing.PushClip(new RectangleGeometry(bounds));
            drawing.PushOpacity(motion.Opacity);
            drawing.PushTransform(new TranslateTransform(0, translate ? motion.Y : 0));
        }

        private static void EndContentDrawing(DrawingContext drawing)
        {
            drawing.Pop(); drawing.Pop(); drawing.Pop();
        }
    }
}
