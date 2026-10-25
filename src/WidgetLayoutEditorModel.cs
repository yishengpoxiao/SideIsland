using System;
using System.Collections.Generic;
using System.Linq;

namespace CodexIsland
{
    internal sealed class WidgetCardDefinition
    {
        public string Id = Guid.NewGuid().ToString("N"), Key = "clock", Size = "standard";
        internal WidgetCardDefinition Copy() { return (WidgetCardDefinition)MemberwiseClone(); }
        internal double Scale { get { return Size == "small" ? .84 : Size == "large" ? 1.06 : 1; } }
    }
    internal sealed class WidgetLayoutDefinition
    {
        public string ProfileId = "";
        public bool Enabled;
        public int RotateSeconds;
        public bool NotificationOverride = true, FocusOverride = true, ActivityOverride = true;
        public int NotificationPriority = 90, FocusPriority = 80, ActivityPriority = 70;
        public List<WidgetCardDefinition> Cards = new List<WidgetCardDefinition>();
        internal WidgetLayoutDefinition Copy()
        {
            return new WidgetLayoutDefinition { ProfileId = ProfileId, Enabled = Enabled, RotateSeconds = RotateSeconds, NotificationOverride = NotificationOverride, FocusOverride = FocusOverride, ActivityOverride = ActivityOverride, NotificationPriority = NotificationPriority, FocusPriority = FocusPriority, ActivityPriority = ActivityPriority, Cards = (Cards ?? new List<WidgetCardDefinition>()).Where(c => c != null).Select(c => c.Copy()).ToList() };
        }
    }
    internal sealed class WidgetLayoutData { public List<WidgetLayoutDefinition> Layouts = new List<WidgetLayoutDefinition>(); }
    internal static class WidgetLayoutPolicy
    {
        internal static bool Valid(WidgetLayoutDefinition layout)
        {
            if (layout == null || layout.ProfileId == null || layout.ProfileId.Length > 64 || layout.ProfileId.Any(Char.IsControl) || layout.Cards == null || layout.Cards.Count < 2 || layout.Cards.Count > 6) return false;
            if (layout.RotateSeconds != 0 && (layout.RotateSeconds < 5 || layout.RotateSeconds > 60)) return false;
            if (layout.NotificationPriority < 0 || layout.NotificationPriority > 100 || layout.FocusPriority < 0 || layout.FocusPriority > 100 || layout.ActivityPriority < 0 || layout.ActivityPriority > 100) return false;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var card in layout.Cards)
            {
                Guid id;
                if (card == null || !Guid.TryParseExact(card.Id, "N", out id) || !ids.Add(card.Id) || !CompactWidgetCatalog.Keys.Contains(card.Key) || !new[] { "small", "standard", "large" }.Contains(card.Size)) return false;
            }
            return true;
        }
        internal static WidgetLayoutDefinition Default(string profileId, Settings settings)
        {
            return new WidgetLayoutDefinition { ProfileId = profileId ?? "", Cards = new List<WidgetCardDefinition> {
                new WidgetCardDefinition { Key = CompactWidgetCatalog.Normalize(settings.CompactSlot1, "codex-primary") },
                new WidgetCardDefinition { Key = CompactWidgetCatalog.Normalize(settings.CompactSlot2, "codex-secondary") } } };
        }
        internal static WidgetCardDefinition[] Visible(WidgetLayoutDefinition layout, DateTime utc, DateTime origin)
        {
            if (!Valid(layout)) throw new ArgumentException("Invalid widget layout.");
            int start = 0;
            if (layout.RotateSeconds > 0) start = (int)((Math.Floor(Math.Max(0, (utc - origin).TotalSeconds) / layout.RotateSeconds) % layout.Cards.Count) * 2 % layout.Cards.Count);
            return new[] { layout.Cards[start], layout.Cards[(start + 1) % layout.Cards.Count] };
        }
    }
    internal sealed class WidgetLayoutStore
    {
        private readonly LocalDataFile<WidgetLayoutData> file;
        internal WidgetLayoutData Data { get; private set; }
        internal WidgetLayoutStore(bool demo)
        {
            file = new LocalDataFile<WidgetLayoutData>("widget-layouts.json", demo);
            Data = file.Load(new WidgetLayoutData()); var old = Data.Layouts;
            Data.Layouts = (old ?? new List<WidgetLayoutDefinition>()).Where(WidgetLayoutPolicy.Valid).GroupBy(x => x.ProfileId, StringComparer.Ordinal).Select(g => g.First()).Take(24).ToList();
            if (old == null || old.Count != Data.Layouts.Count) file.MarkInvalid();
        }
        internal WidgetLayoutDefinition Resolve(string profileId)
        {
            return Data.Layouts.FirstOrDefault(l => l.ProfileId == (profileId ?? "")) ?? Data.Layouts.FirstOrDefault(l => l.ProfileId.Length == 0);
        }
        internal void Save(WidgetLayoutDefinition value)
        {
            if (!WidgetLayoutPolicy.Valid(value)) throw new ArgumentException("组件布局无效。");
            var next = Data.Layouts.Where(x => x.ProfileId != value.ProfileId).Select(x => x.Copy()).ToList();
            if (next.Count >= 24) throw new ArgumentException("组件布局数量超过限制。");
            next.Add(value.Copy()); var data = new WidgetLayoutData { Layouts = next }; file.Save(data); Data = data;
        }
    }
}
