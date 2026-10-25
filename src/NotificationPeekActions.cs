namespace CodexIsland
{
    internal partial class IslandWindow
    {
        // Remove preview data before changing mode so a clear/block/privacy
        // action cannot capture that data into a new outgoing bitmap.
        private void DismissNotificationPeek(bool animate)
        {
            bool wasPeeking = peeking;
            EndPeek();
            if (!wasPeeking) return;
            DiscardTransitionSnapshot();
            if (!shuttingDown) SetExpanded(false, animate && IsVisible);
        }
    }
}
