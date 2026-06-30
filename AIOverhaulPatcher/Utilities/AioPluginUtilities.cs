using System;
using System.Collections.Generic;
using System.Linq;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Synthesis;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace AIOverhaulPatcher.Utilities
{
    public readonly record struct AioPackageMergeResult(
        List<FormKey> EffectiveAioOrder,
        Dictionary<string, List<FormKey>> LayersByModFileName);

    public static class AioPluginUtilities
    {
        public const string UssepPatchFileName = "AI Overhaul - USSEP Patch.esp";

        public static readonly string[] PluginFileNames =
        {
            "AI Overhaul.esp",
            UssepPatchFileName,
            "AI Overhaul - Fishing Addon.esp",
            "AI Overhaul - Scripted Addon.esp",
        };

        public static List<ISkyrimModGetter> GetLoadedAioMods(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
        {
            return PluginFileNames
                .Select(name => state.LoadOrder.GetModByFileName(name))
                .Where(m => m != null)
                .Cast<ISkyrimModGetter>()
                .ToList();
        }

        public static HashSet<ModKey> GetAioModKeys(IEnumerable<ISkyrimModGetter> aioMods)
            => aioMods.Select(m => m.ModKey).ToHashSet();

        public static INpcGetter? GetWinningAioNpc(
            FormKey formKey,
            IPatcherState<ISkyrimMod, ISkyrimModGetter> state,
            HashSet<ModKey> aioModKeys)
        {
            var modKey = GetWinningAioModKey(formKey, state, aioModKeys);
            if (modKey == null) return null;
            return state.LoadOrder[modKey.Value].Mod?.Npcs.FirstOrDefault(n => n.FormKey == formKey);
        }

        public static ModKey? GetWinningAioModKey(
            FormKey formKey,
            IPatcherState<ISkyrimMod, ISkyrimModGetter> state,
            HashSet<ModKey> aioModKeys)
        {
            foreach (var listing in state.LoadOrder.PriorityOrder.Reverse())
            {
                if (!aioModKeys.Contains(listing.ModKey)) continue;
                if (listing.Mod?.Npcs.FirstOrDefault(n => n.FormKey == formKey) != null)
                    return listing.ModKey;
            }
            return null;
        }

        public static List<FormKey> GetAllAioNpcFormKeys(IEnumerable<ISkyrimModGetter> aioMods)
            => aioMods.SelectMany(m => m.Npcs).Select(n => n.FormKey).Distinct().ToList();

        public static AioPackageMergeResult BuildEffectiveAioPackageOrder(
            FormKey formKey,
            IPatcherState<ISkyrimMod, ISkyrimModGetter> state,
            HashSet<ModKey> aioModKeys)
        {
            var packagesByAioMod = state.LinkCache
                .ResolveAllContexts<INpc, INpcGetter>(formKey, ResolveTarget.Winner)
                .Where(c => aioModKeys.Contains(c.ModKey))
                .GroupBy(c => c.ModKey)
                .ToDictionary(
                    g => g.Key,
                    g => g.First().Record.Packages.Select(p => p.FormKey).ToList());

            foreach (var listing in state.LoadOrder.PriorityOrder)
            {
                if (!aioModKeys.Contains(listing.ModKey)) continue;
                if (packagesByAioMod.ContainsKey(listing.ModKey)) continue;
                var npc = listing.Mod?.Npcs.FirstOrDefault(n => n.FormKey == formKey);
                if (npc == null) continue;
                packagesByAioMod[listing.ModKey] = npc.Packages.Select(p => p.FormKey).ToList();
            }

            var layersByModFileName = new Dictionary<string, List<FormKey>>();
            List<FormKey>? cumulative = null;
            foreach (var listing in state.LoadOrder.PriorityOrder)
            {
                if (!packagesByAioMod.TryGetValue(listing.ModKey, out var patchPackages)) continue;
                layersByModFileName[listing.ModKey.FileName] = patchPackages;
                cumulative = cumulative == null
                    ? patchPackages
                    : MergeAioPackageLayer(cumulative, patchPackages);
            }

            return new AioPackageMergeResult(cumulative ?? new List<FormKey>(), layersByModFileName);
        }

        internal static List<FormKey> MergeAioPackageLayer(List<FormKey> previous, List<FormKey> patch)
        {
            if (patch.Count == 0) return previous;

            if (previous.All(p => patch.Contains(p)))
                return new List<FormKey>(patch);

            var result = new List<FormKey>(previous);
            foreach (var pkg in patch)
            {
                if (result.Contains(pkg)) continue;
                result.Insert(DetermineInsertionIndex(pkg, patch, result), pkg);
            }

            return result;
        }

        internal static int DetermineInsertionIndex(FormKey pkg, List<FormKey> patch, List<FormKey> current)
        {
            var pkgIndex = patch.IndexOf(pkg);

            for (int i = pkgIndex + 1; i < patch.Count; i++)
            {
                var idx = current.IndexOf(patch[i]);
                if (idx >= 0) return idx;
            }

            for (int i = pkgIndex - 1; i >= 0; i--)
            {
                var idx = current.IndexOf(patch[i]);
                if (idx >= 0) return idx + 1;
            }

            return 0;
        }

        public static List<FormKey> BuildMergedPackageList(
            IReadOnlyList<FormKey> aioPackageOrder,
            INpcGetter winningOverride,
            IEnumerable<INpcGetter> masterNpcs,
            IEnumerable<INpcGetter> postUssepOverrides)
        {
            return BuildMergedPackageList(
                aioPackageOrder,
                winningOverride.Packages.Select(x => x.FormKey).ToList(),
                masterNpcs.Select(m => m.Packages.Select(x => x.FormKey).ToList()),
                postUssepOverrides.Select(o => o.Packages.Select(x => x.FormKey).ToList()));
        }

        public static List<FormKey> BuildMergedPackageList(
            IReadOnlyList<FormKey> aioPackageOrder,
            IReadOnlyList<FormKey> winningOverridePackages,
            IEnumerable<IReadOnlyList<FormKey>> masterPackageLists,
            IEnumerable<IReadOnlyList<FormKey>> postUssepOverridePackageLists)
        {
            var aioPackageSet = aioPackageOrder.ToHashSet();

            var packagesToRemove = masterPackageLists
                .SelectMany(x => x)
                .Where(x => !aioPackageSet.Contains(x))
                .ToHashSet();

            var candidateKeys = aioPackageOrder
                .Concat(postUssepOverridePackageLists.SelectMany(x => x))
                .Where(x => !packagesToRemove.Contains(x))
                .ToHashSet();

            var result = aioPackageOrder.Where(candidateKeys.Contains).ToList();

            foreach (var key in winningOverridePackages)
            {
                if (!aioPackageSet.Contains(key) && candidateKeys.Contains(key) && !result.Contains(key))
                    result.Add(key);
            }

            return result;
        }

        public static bool PackageListsEqual(
            IEnumerable<IFormLinkGetter> current,
            IReadOnlyList<FormKey> target)
        {
            return current.Select(x => x.FormKey).SequenceEqual(target);
        }

        public static bool PackageListsEqual(
            IReadOnlyList<FormKey> current,
            IReadOnlyList<FormKey> target)
        {
            return current.SequenceEqual(target);
        }

        public static string FormatPackageList(IReadOnlyList<FormKey> packages)
        {
            if (packages.Count == 0) return "(empty)";
            return string.Join(", ", packages.Select((p, i) => $"#{i}:{p}"));
        }

        public static bool ShouldForwardAioChange<T>(T master, T aio, T winning)
        {
            if (EqualityComparer<T>.Default.Equals(aio, master)) return false;
            if (EqualityComparer<T>.Default.Equals(winning, aio)) return false;
            if (!EqualityComparer<T>.Default.Equals(winning, master)
                && !EqualityComparer<T>.Default.Equals(winning, aio)) return false;
            return true;
        }

        public static bool ShouldForwardAioFormLink<TMajor>(
            IFormLinkGetter<TMajor> master,
            IFormLinkGetter<TMajor> aio,
            IFormLinkGetter<TMajor> winning)
            where TMajor : class, IMajorRecordGetter
            => ShouldForwardAioChange(master.FormKey, aio.FormKey, winning.FormKey);

        public static bool PlayerSkillsEqual(IPlayerSkillsGetter? left, IPlayerSkillsGetter? right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null) return false;
            if (left.Health != right.Health || left.Magicka != right.Magicka || left.Stamina != right.Stamina)
                return false;
            if (!left.SkillValues.SequenceEqual(right.SkillValues)) return false;
            return left.SkillOffsets.SequenceEqual(right.SkillOffsets);
        }

        public static bool ShouldForwardAioPlayerSkills(
            IPlayerSkillsGetter? master,
            IPlayerSkillsGetter? aio,
            IPlayerSkillsGetter? winning)
            => ShouldForwardAioChange(master, aio, winning, PlayerSkillsEqual);

        public static bool ShouldForwardAioChange<T>(
            T? master,
            T? aio,
            T? winning,
            Func<T?, T?, bool> equals)
        {
            if (equals(aio, master)) return false;
            if (equals(winning, aio)) return false;
            if (!equals(winning, master) && !equals(winning, aio)) return false;
            return true;
        }

        public static bool ShouldForwardAioObjectBounds(
            IObjectBoundsGetter master,
            IObjectBoundsGetter aio,
            IObjectBoundsGetter winning)
            => ShouldForwardAioChange(
                master,
                aio,
                winning,
                (left, right) => left != null && right != null && left.Equals(right));

        public static void ForwardAioChangedFlagBits(
            NpcConfiguration.Flag masterFlags,
            NpcConfiguration.Flag aioFlags,
            NpcConfiguration.Flag winningFlags,
            Action<NpcConfiguration.Flag> setFlags)
        {
            var changedByAio = aioFlags ^ masterFlags;
            if (changedByAio == 0) return;

            var merged = winningFlags;
            foreach (NpcConfiguration.Flag flag in Enum.GetValues(typeof(NpcConfiguration.Flag)))
            {
                if (flag == 0 || !changedByAio.HasFlag(flag)) continue;
                merged = aioFlags.HasFlag(flag)
                    ? merged.SetFlag(flag, true)
                    : merged.SetFlag(flag, false);
            }

            if (merged != winningFlags)
                setFlags(merged);
        }

        public static void ForwardAioChangedTemplateFlagBits(
            NpcConfiguration.TemplateFlag masterFlags,
            NpcConfiguration.TemplateFlag aioFlags,
            NpcConfiguration.TemplateFlag winningFlags,
            Action<NpcConfiguration.TemplateFlag> setFlags)
        {
            var changedByAio = aioFlags ^ masterFlags;
            if (changedByAio == 0) return;

            var merged = winningFlags;
            foreach (NpcConfiguration.TemplateFlag flag in Enum.GetValues(typeof(NpcConfiguration.TemplateFlag)))
            {
                if (flag == 0 || !changedByAio.HasFlag(flag)) continue;
                merged = aioFlags.HasFlag(flag)
                    ? merged.SetFlag(flag, true)
                    : merged.SetFlag(flag, false);
            }

            if (merged != winningFlags)
                setFlags(merged);
        }
    }
}
