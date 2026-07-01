namespace AIOverhaulPatcher.Settings
{
    public enum QuestAlpcPatchMode { Off, Mg01Only, All }

    public class Settings
    {
        public bool IgnoreIdenticalToLastOverride { get; set; } = false;
        public bool IgnorePlayerRecord { get; set; } = true;
        public bool MaintainHighestProtectionLevel { get; set; } = true;
        public bool MergeItems { get; set; } = true;
        public QuestAlpcPatchMode QuestAlpcPatch { get; set; } = QuestAlpcPatchMode.Off;
        public bool LogPackageMerge { get; set; } = true;
        public bool LogPackageMergeAllNpcs { get; set; } = false;
    }
}
