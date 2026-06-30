using System;
using System.Collections.Generic;
using System.Linq;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Synthesis;
using Mutagen.Bethesda.Skyrim;

namespace AIOverhaulPatcher.Utilities
{
    public static class AioQuestAlpcUtilities
    {
        public static IReadOnlyList<FormKey> GetReferenceAliasPackages(IQuestGetter quest, uint aliasId)
        {
            var alias = TryGetReferenceAliasGetter(quest, aliasId);
            if (alias == null) return Array.Empty<FormKey>();
            return alias.PackageData.Select(p => p.FormKey).ToList();
        }

        public static IQuestAliasGetter? TryGetReferenceAliasGetter(IQuestGetter quest, uint aliasId)
        {
            return quest.Aliases
                .FirstOrDefault(a => a.Type == QuestAlias.TypeEnum.Reference && a.ID == aliasId);
        }

        public static QuestAlias? TryGetReferenceAlias(IQuest quest, uint aliasId)
        {
            return quest.Aliases
                .FirstOrDefault(a => a.Type == QuestAlias.TypeEnum.Reference && a.ID == aliasId);
        }

        public static void SetReferenceAliasPackages(QuestAlias alias, IReadOnlyList<FormKey> packages)
        {
            alias.PackageData.Clear();
            foreach (var key in packages)
                alias.PackageData.Add(new FormLink<IPackageGetter>(key));
        }

        public static IQuestGetter? GetWinningAioQuest(
            FormKey formKey,
            IPatcherState<ISkyrimMod, ISkyrimModGetter> state,
            HashSet<ModKey> aioModKeys)
        {
            var modKey = GetWinningAioQuestModKey(formKey, state, aioModKeys);
            if (modKey == null) return null;
            return state.LoadOrder[modKey.Value].Mod?.Quests.FirstOrDefault(q => q.FormKey == formKey);
        }

        public static ModKey? GetWinningAioQuestModKey(
            FormKey formKey,
            IPatcherState<ISkyrimMod, ISkyrimModGetter> state,
            HashSet<ModKey> aioModKeys)
        {
            foreach (var listing in state.LoadOrder.PriorityOrder.Reverse())
            {
                if (!aioModKeys.Contains(listing.ModKey)) continue;
                if (listing.Mod?.Quests.FirstOrDefault(q => q.FormKey == formKey) != null)
                    return listing.ModKey;
            }
            return null;
        }

        public static List<FormKey> GetAllAioQuestFormKeys(IEnumerable<ISkyrimModGetter> aioMods)
            => aioMods.SelectMany(m => m.Quests).Select(q => q.FormKey).Distinct().ToList();

        public static AioPackageMergeResult BuildEffectiveAioAliasPackageOrder(
            FormKey questFormKey,
            uint aliasId,
            IPatcherState<ISkyrimMod, ISkyrimModGetter> state,
            HashSet<ModKey> aioModKeys)
        {
            var packagesByAioMod = state.LinkCache
                .ResolveAllContexts<IQuest, IQuestGetter>(questFormKey, ResolveTarget.Winner)
                .Where(c => aioModKeys.Contains(c.ModKey))
                .GroupBy(c => c.ModKey)
                .ToDictionary(
                    g => g.Key,
                    g => GetReferenceAliasPackages(g.First().Record, aliasId));

            foreach (var listing in state.LoadOrder.PriorityOrder)
            {
                if (!aioModKeys.Contains(listing.ModKey)) continue;
                if (packagesByAioMod.ContainsKey(listing.ModKey)) continue;
                var quest = listing.Mod?.Quests.FirstOrDefault(q => q.FormKey == questFormKey);
                if (quest == null) continue;
                packagesByAioMod[listing.ModKey] = GetReferenceAliasPackages(quest, aliasId);
            }

            var layersByModFileName = new Dictionary<string, List<FormKey>>();
            List<FormKey>? cumulative = null;
            foreach (var listing in state.LoadOrder.PriorityOrder)
            {
                if (!packagesByAioMod.TryGetValue(listing.ModKey, out var patchPackagesReadOnly)) continue;
                var patchPackages = patchPackagesReadOnly.ToList();
                layersByModFileName[listing.ModKey.FileName] = patchPackages;
                cumulative = cumulative == null
                    ? patchPackages
                    : AioPluginUtilities.MergeAioPackageLayer(cumulative, patchPackages);
            }

            return new AioPackageMergeResult(cumulative ?? new List<FormKey>(), layersByModFileName);
        }

        public static IEnumerable<IQuestAliasGetter> GetReferenceAliases(IQuestGetter quest)
            => quest.Aliases.Where(a => a.Type == QuestAlias.TypeEnum.Reference);
    }
}
