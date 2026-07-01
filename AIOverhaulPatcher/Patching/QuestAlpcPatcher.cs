using System;
using System.Collections.Generic;
using System.Linq;
using AIOverhaulPatcher.Utilities;
using Mutagen.Bethesda;
using Mutagen.Bethesda.FormKeys.SkyrimSE;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Synthesis;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace AIOverhaulPatcher.Patching
{
    public static class QuestAlpcPatcher
    {
        private const uint Mg01FaraldaAliasId = 11;

        public static void PatchMg01(
            IPatcherState<ISkyrimMod, ISkyrimModGetter> state,
            HashSet<ModKey> aioModKeys,
            int ussepOrder,
            Settings.Settings settings,
            ISkyrimModGetter aiOverhaul)
        {
            PatchQuest(
                state,
                aioModKeys,
                ussepOrder,
                settings,
                aiOverhaul,
                Skyrim.Quest.MG01.FormKey,
                new HashSet<uint> { Mg01FaraldaAliasId },
                "MG01",
                logSkips: true);
        }

        public static void PatchAll(
            IPatcherState<ISkyrimMod, ISkyrimModGetter> state,
            HashSet<ModKey> aioModKeys,
            int ussepOrder,
            Settings.Settings settings,
            ISkyrimModGetter aiOverhaul,
            IEnumerable<ISkyrimModGetter> aioMods)
        {
            var aioQuestFormKeys = AioQuestAlpcUtilities.GetAllAioQuestFormKeys(aioMods);
            var winningQuestOverrides = state.LoadOrder.PriorityOrder
                .WinningOverrides<IQuestGetter>()
                .ToDictionary(q => q.FormKey);

            int processed = 0;
            int total = aioQuestFormKeys.Count;
            int updatedQuestCount = 0;
            int updatedAliasCount = 0;
            int bmax = 10;
            int b = 0;

            Console.WriteLine($"0/{total} Quests (ALPC)");
            foreach (var questFormKey in aioQuestFormKeys)
            {
                if (b >= bmax)
                {
                    b = 0;
                    Console.WriteLine($"{processed}/{total} Quests (ALPC)");
                }

                if (!winningQuestOverrides.TryGetValue(questFormKey, out var winningOverride))
                {
                    processed++;
                    b++;
                    continue;
                }

                var result = PatchQuest(
                    state,
                    aioModKeys,
                    ussepOrder,
                    settings,
                    aiOverhaul,
                    questFormKey,
                    aliasFilter: null,
                    questLabel: null,
                    winningOverride: winningOverride);

                updatedQuestCount += result.QuestUpdated ? 1 : 0;
                updatedAliasCount += result.AliasesUpdated;
                processed++;
                b++;
            }

            Console.WriteLine($"Updated {updatedQuestCount} quest(s), {updatedAliasCount} alias ALPC list(s).");
        }

        private static (bool QuestUpdated, int AliasesUpdated) PatchQuest(
            IPatcherState<ISkyrimMod, ISkyrimModGetter> state,
            HashSet<ModKey> aioModKeys,
            int ussepOrder,
            Settings.Settings settings,
            ISkyrimModGetter aiOverhaul,
            FormKey questFormKey,
            HashSet<uint>? aliasFilter,
            string? questLabel,
            bool logSkips = false,
            IQuestGetter? winningOverride = null)
        {
            var aioQuest = AioQuestAlpcUtilities.GetWinningAioQuest(questFormKey, state, aioModKeys);
            if (aioQuest == null)
            {
                if (logSkips)
                    Console.WriteLine($"Quest ALPC skip {questFormKey}: no AIO override found");
                return (false, 0);
            }

            winningOverride ??= state.LoadOrder.PriorityOrder
                .WinningOverrides<IQuestGetter>()
                .FirstOrDefault(q => q.FormKey == questFormKey);
            if (winningOverride == null)
            {
                if (logSkips)
                    Console.WriteLine($"Quest ALPC skip {questFormKey}: no winning override found");
                return (false, 0);
            }

            var masterFilenames = aiOverhaul.MasterReferences.Select(x => x.Master.FileName).ToList();
            var masterFiles = state.LoadOrder.PriorityOrder.Reverse()
                .Where(x => masterFilenames.Contains(x.ModKey.FileName))
                .ToList();
            var questMasters = masterFiles.Select(x => x.Mod).NotNull()
                .SelectMany(x => x.Quests)
                .Where(x => x.FormKey == questFormKey)
                .ToList();

            var winningMaster = questMasters.FirstOrDefault();
            if (winningMaster == null)
            {
                winningMaster = state.LoadOrder.PriorityOrder.Select(x => x.Mod).NotNull()
                    .SelectMany(x => x.Quests)
                    .FirstOrDefault(x => x.FormKey == questFormKey);
            }
            if (winningMaster == null)
            {
                if (logSkips)
                    Console.WriteLine($"Quest ALPC skip {questFormKey}: no master quest found");
                return (false, 0);
            }

            questLabel ??= FormatQuestLabel(winningOverride);

            Quest? patchQuest = null;
            bool questChanged = false;
            int aliasesUpdated = 0;

            foreach (var aioAlias in AioQuestAlpcUtilities.GetReferenceAliases(aioQuest))
            {
                if (aliasFilter != null && !aliasFilter.Contains(aioAlias.ID))
                    continue;

                var masterAlias = AioQuestAlpcUtilities.TryGetReferenceAliasGetter(winningMaster, aioAlias.ID);
                if (masterAlias == null)
                    continue;

                var masterPackages = masterAlias.PackageData.Select(p => p.FormKey).ToList();
                var aioPackages = aioAlias.PackageData.Select(p => p.FormKey).ToList();
                if (AioPluginUtilities.PackageListsEqual(masterPackages, aioPackages))
                    continue;

                var aliasId = aioAlias.ID;
                var aioPackageOrder = AioQuestAlpcUtilities.BuildEffectiveAioAliasPackageOrder(
                    questFormKey, aliasId, state, aioModKeys);
                var winnerPackages = AioQuestAlpcUtilities.GetReferenceAliasPackages(winningOverride, aliasId);
                var masterPackageLists = questMasters
                    .Select(m => AioQuestAlpcUtilities.GetReferenceAliasPackages(m, aliasId))
                    .ToList();

                var packageMerge = AioPluginUtilities.BuildMergedPackageList(
                    state.LinkCache,
                    aioPackageOrder.EffectiveAioOrder,
                    winnerPackages,
                    masterPackageLists);
                var mergedPackages = packageMerge.Packages;

                if (AioPluginUtilities.PackageListsEqual(winnerPackages, mergedPackages))
                    continue;

                patchQuest ??= state.PatchMod.Quests.GetOrAddAsOverride(winningOverride);
                var patchAlias = AioQuestAlpcUtilities.TryGetReferenceAlias(patchQuest, aliasId);
                if (patchAlias == null)
                {
                    Console.WriteLine($"Quest ALPC skip {questLabel} alias {aliasId}: alias missing on winning override");
                    continue;
                }

                var beforePackages = patchAlias.PackageData.Select(p => p.FormKey).ToList();
                AioQuestAlpcUtilities.SetReferenceAliasPackages(patchAlias, mergedPackages);
                questChanged = true;
                aliasesUpdated++;

                Console.WriteLine(
                    $"Updated {questLabel} alias {aliasId} ALPC: before=[{AioPluginUtilities.FormatPackageList(beforePackages)}] merged=[{AioPluginUtilities.FormatPackageList(mergedPackages)}]");
            }

            if (patchQuest != null)
            {
                if (settings.IgnoreIdenticalToLastOverride && !questChanged)
                    state.PatchMod.Quests.Remove(patchQuest);
                else if (questChanged)
                    Console.WriteLine($"Patched quest override: {questLabel}");
            }

            return (questChanged, aliasesUpdated);
        }

        private static string FormatQuestLabel(IQuestGetter quest)
        {
            try
            {
                if (quest.EditorID != null)
                    return $"{quest.EditorID} ({quest.FormKey})";
            }
            catch { }
            return quest.FormKey.ToString();
        }
    }
}
