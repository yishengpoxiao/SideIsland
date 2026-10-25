using System;
using System.Collections.Generic;
using System.Linq;

namespace CodexIsland
{
    public sealed class LayoutProfile
    {
        public string Id = "", Name = "", Mode = "quota", Slot1 = "codex-primary", Slot2 = "codex-secondary", Tool = "focus", Favorites = "focus,notes,reminders";
        public bool Quiet, AutoMusic;
        public static LayoutProfile Capture(Settings settings, string name)
        { return new LayoutProfile { Id = Guid.NewGuid().ToString("N"), Name = name, Mode = settings.CompactMode, Slot1 = settings.CompactSlot1, Slot2 = settings.CompactSlot2, Quiet = settings.QuietPreviews, AutoMusic = settings.AutoMusic, Tool = settings.LastTool, Favorites = settings.FavoriteTools }; }
        internal void Apply(Settings settings)
        {
            settings.CompactMode = new[] { "quota", "clock", "focus", "music" }.Contains(Mode) ? Mode : "quota";
            settings.CompactSlot1 = CompactWidgetCatalog.Normalize(Slot1, "codex-primary"); settings.CompactSlot2 = CompactWidgetCatalog.Normalize(Slot2, "codex-secondary");
            settings.QuietPreviews = Quiet; settings.AutoMusic = AutoMusic; settings.LastTool = ToolCatalog.Contains(Tool) ? Tool : "focus";
            settings.FavoriteTools = ToolCatalog.NormalizeFavorites(Favorites);
        }
    }
    internal sealed class LayoutProfileData
    {
        public List<LayoutProfile> Profiles = new List<LayoutProfile>();
        public string ActiveId = "";
        public LayoutProfile Previous;
        public bool RestoreAfterFocus, FocusRestoreArmed;
    }
    internal sealed class LayoutProfileStore
    {
        private readonly LocalDataFile<LayoutProfileData> file;
        internal LayoutProfileData Data { get; private set; }
        internal LayoutProfileStore(bool demo)
        {
            file = new LocalDataFile<LayoutProfileData>("layout-profiles.json", demo);
            Data = file.Load(new LayoutProfileData());
            var original = Data.Profiles;
            Data.Profiles = (original ?? new List<LayoutProfile>()).Where(ValidCustom).GroupBy(p => p.Id).Select(g => g.First()).Take(20).ToList();
            if (original == null || original.Count != Data.Profiles.Count) file.MarkInvalid();
            if (!String.IsNullOrEmpty(Data.ActiveId) && Find(Data.ActiveId) == null) Data.ActiveId = "";
        }
        private static bool ValidCustom(LayoutProfile profile)
        { Guid id; return profile != null && Guid.TryParseExact(profile.Id, "N", out id) && !String.IsNullOrWhiteSpace(profile.Name) && profile.Name.Length <= 60 && !profile.Name.Any(Char.IsControl); }
        internal static bool IsBuiltin(string id) { return id == "code" || id == "music" || id == "focus"; }
        internal static List<LayoutProfile> Builtins()
        {
            return new List<LayoutProfile> {
                new LayoutProfile { Id="code", Name="写代码", Slot1="codex-secondary", Slot2="ports", Tool="ports", Favorites="ports,logwatch,notes", AutoMusic=false },
                new LayoutProfile { Id="music", Name="听音乐", Mode="music", Tool="media", Favorites="media,focus,notes", AutoMusic=true },
                new LayoutProfile { Id="focus", Name="专注", Mode="focus", Tool="focus", Favorites="focus,notes,reminders", Quiet=true, AutoMusic=false }
            };
        }
        internal IEnumerable<LayoutProfile> All { get { return Builtins().Concat(Data.Profiles); } }
        internal LayoutProfile Find(string id) { return All.FirstOrDefault(x => x.Id == id); }
        internal void Save() { file.Save(Data); }
    }
    internal static class TemporaryLayoutPolicy
    {
        internal static bool Matches(LayoutProfile profile, Settings current)
        {
            if (profile == null || current == null) return false;
            return current.CompactMode == profile.Mode && current.CompactSlot1 == profile.Slot1 && current.CompactSlot2 == profile.Slot2 && current.QuietPreviews == profile.Quiet && current.AutoMusic == profile.AutoMusic && current.FavoriteTools == ToolCatalog.NormalizeFavorites(profile.Favorites);
        }
    }
}
