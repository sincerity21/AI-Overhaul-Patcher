using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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

    public readonly record struct PackageMergeResult(
        List<FormKey> Packages,
        List<string> Events);

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
            ILinkCache linkCache,
            IReadOnlyList<FormKey> aioPackageOrder,
            INpcGetter winningOverride,
            IEnumerable<INpcGetter> masterNpcs)
        {
            return BuildMergedPackageList(
                linkCache,
                aioPackageOrder,
                winningOverride.Packages.Select(x => x.FormKey).ToList(),
                masterNpcs.Select(m => m.Packages.Select(x => x.FormKey).ToList())).Packages;
        }

        public static PackageMergeResult BuildMergedPackageList(
            ILinkCache linkCache,
            IReadOnlyList<FormKey> aioPackageOrder,
            IReadOnlyList<FormKey> winningOverridePackages,
            IEnumerable<IReadOnlyList<FormKey>> masterPackageLists)
        {
            var events = new List<string>();
            var editorIdCache = new Dictionary<FormKey, string?>();

            string? EditorId(FormKey key) => GetPackageEditorId(linkCache, key, editorIdCache);

            var aioByEditorId = new Dictionary<string, FormKey>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in aioPackageOrder)
            {
                var editorId = EditorId(key);
                if (editorId != null)
                    aioByEditorId[editorId] = key;
            }

            var aioEditorIds = aioByEditorId.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var masterEditorIds = masterPackageLists
                .SelectMany(x => x)
                .Select(EditorId)
                .Where(x => x != null)
                .Cast<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var winningEditorIds = winningOverridePackages
                .Select(EditorId)
                .Where(x => x != null)
                .Cast<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var result = new List<FormKey>(winningOverridePackages);

            for (int i = 0; i < result.Count; i++)
            {
                var editorId = EditorId(result[i]);
                if (editorId == null) continue;
                if (!aioByEditorId.TryGetValue(editorId, out var aioKey)) continue;
                if (aioKey == result[i]) continue;
                events.Add($"substitute EditorID={editorId}: {result[i]} -> {aioKey}");
                result[i] = aioKey;
            }

            for (int i = result.Count - 1; i >= 0; i--)
            {
                var editorId = EditorId(result[i]);
                if (editorId == null) continue;
                if (!ShouldStripMasterPackageRemovedByAio(editorId, masterEditorIds, aioEditorIds, winningEditorIds))
                    continue;
                events.Add($"strip AIO-removed EditorID={editorId} ({result[i]})");
                result.RemoveAt(i);
            }

            var resultEditorIds = result
                .Select(EditorId)
                .Where(x => x != null)
                .Cast<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var aioKey in aioPackageOrder)
            {
                var editorId = EditorId(aioKey);
                if (editorId == null) continue;
                if (resultEditorIds.Contains(editorId)) continue;

                if (!ShouldForwardAioPackage(editorId, masterEditorIds, aioEditorIds, winningEditorIds))
                {
                    events.Add($"skip insert EditorID={editorId} ({aioKey}): winner removed");
                    continue;
                }

                var insertAt = DetermineInsertionIndexByEditorId(aioKey, aioPackageOrder, result, linkCache, editorIdCache);
                events.Add($"insert EditorID={editorId} ({aioKey}) at index {insertAt}");
                result.Insert(insertAt, aioKey);
                resultEditorIds.Add(editorId);
            }

            var deduped = new List<FormKey>(result.Count);
            var seenEditorIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in result)
            {
                var editorId = EditorId(key);
                if (editorId != null)
                {
                    if (!seenEditorIds.Add(editorId))
                    {
                        events.Add($"dedupe EditorID={editorId} ({key})");
                        continue;
                    }
                }
                deduped.Add(key);
            }
            result = deduped;

            return new PackageMergeResult(result, events);
        }

        internal static bool ShouldForwardAioPackage(
            string editorId,
            HashSet<string> masterEditorIds,
            HashSet<string> aioEditorIds,
            HashSet<string> winningEditorIds)
        {
            if (!aioEditorIds.Contains(editorId)) return false;
            if (masterEditorIds.Contains(editorId) && !winningEditorIds.Contains(editorId))
                return false;
            return true;
        }

        /// <summary>
        /// Strip master packages AIO removed unless the winner already merged AIO and kept the package
        /// (e.g. JOJ re-adding WhiterunVignarDrunkenHuntsman on top of AIO packages).
        /// </summary>
        internal static bool ShouldStripMasterPackageRemovedByAio(
            string editorId,
            HashSet<string> masterEditorIds,
            HashSet<string> aioEditorIds,
            HashSet<string> winningEditorIds)
        {
            if (!masterEditorIds.Contains(editorId)) return false;
            if (aioEditorIds.Contains(editorId)) return false;
            if (winningEditorIds.Contains(editorId)
                && WinnerAbsorbedAioExclusivePackages(winningEditorIds, aioEditorIds, masterEditorIds))
                return false;
            return true;
        }

        /// <summary>
        /// True when the winner already carries at least one AIO-exclusive package (not on master),
        /// e.g. Vignar with AIOWRGrayFamilyMourn while JOJ re-added a vanilla package.
        /// </summary>
        private static bool WinnerAbsorbedAioExclusivePackages(
            HashSet<string> winningEditorIds,
            HashSet<string> aioEditorIds,
            HashSet<string> masterEditorIds)
        {
            foreach (var editorId in winningEditorIds)
            {
                if (!aioEditorIds.Contains(editorId)) continue;
                if (masterEditorIds.Contains(editorId)) continue;
                return true;
            }
            return false;
        }

        internal static string? GetPackageEditorId(
            ILinkCache linkCache,
            FormKey key,
            Dictionary<FormKey, string?> cache)
        {
            if (cache.TryGetValue(key, out var cached))
                return cached;
            var editorId = linkCache.TryResolve<IPackageGetter>(key, out var pkg) ? pkg.EditorID : null;
            cache[key] = editorId;
            return editorId;
        }

        internal static string? GetSpellEditorId(
            ILinkCache linkCache,
            FormKey key,
            Dictionary<FormKey, string?> cache)
        {
            if (cache.TryGetValue(key, out var cached))
                return cached;
            var editorId = linkCache.TryResolve<ISpellGetter>(key, out var spell) ? spell.EditorID : null;
            cache[key] = editorId;
            return editorId;
        }

        internal static int DetermineInsertionIndexByEditorId(
            FormKey pkg,
            IReadOnlyList<FormKey> aioOrder,
            List<FormKey> current,
            ILinkCache linkCache,
            Dictionary<FormKey, string?> editorIdCache)
        {
            var pkgIndex = aioOrder.IndexOf(pkg);
            if (pkgIndex < 0)
                return DetermineInsertionIndex(pkg, aioOrder.ToList(), current);

            for (int i = pkgIndex + 1; i < aioOrder.Count; i++)
            {
                var idx = FindPackageIndexByEditorIdOrFormKey(aioOrder[i], current, linkCache, editorIdCache);
                if (idx >= 0) return idx;
            }

            for (int i = pkgIndex - 1; i >= 0; i--)
            {
                var idx = FindPackageIndexByEditorIdOrFormKey(aioOrder[i], current, linkCache, editorIdCache);
                if (idx >= 0) return idx + 1;
            }

            return 0;
        }

        internal static int FindPackageIndexByEditorIdOrFormKey(
            FormKey key,
            IReadOnlyList<FormKey> list,
            ILinkCache linkCache,
            Dictionary<FormKey, string?> editorIdCache)
        {
            var idx = list.IndexOf(key);
            if (idx >= 0) return idx;

            var editorId = GetPackageEditorId(linkCache, key, editorIdCache);
            if (editorId == null) return -1;

            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(GetPackageEditorId(linkCache, list[i], editorIdCache), editorId, StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            return -1;
        }

        public static bool ForwardAioFactionChanges(
            Npc patchNpc,
            INpcGetter winningMaster,
            INpcGetter aioNpc,
            INpcGetter winningOverride)
        {
            bool change = false;

            var masterFactionKeys = winningMaster.Factions.Select(f => f.Faction.FormKey).ToHashSet();
            var aioFactionKeys = aioNpc.Factions.Select(f => f.Faction.FormKey).ToHashSet();
            var winnerFactionKeys = winningOverride.Factions.Select(f => f.Faction.FormKey).ToHashSet();

            var masterFactionsByKey = winningMaster.Factions.ToDictionary(f => f.Faction.FormKey);
            var aioFactionsByKey = aioNpc.Factions.ToDictionary(f => f.Faction.FormKey);
            var winnerFactionsByKey = winningOverride.Factions.ToDictionary(f => f.Faction.FormKey);

            foreach (var masterKey in masterFactionKeys)
            {
                if (aioFactionKeys.Contains(masterKey)) continue;
                var existing = patchNpc.Factions.FirstOrDefault(f => f.Faction.FormKey == masterKey);
                if (existing == null) continue;
                patchNpc.Factions.Remove(existing);
                change = true;
            }

            foreach (var aioFac in aioNpc.Factions)
            {
                var factionKey = aioFac.Faction.FormKey;
                if (masterFactionKeys.Contains(factionKey) && !winnerFactionKeys.Contains(factionKey))
                    continue;

                if (!masterFactionKeys.Contains(factionKey))
                {
                    if (patchNpc.Factions.Any(f => f.Faction.FormKey == factionKey)) continue;
                    patchNpc.Factions.Add(aioFac.DeepCopy());
                    change = true;
                }
            }

            foreach (var aioFac in aioNpc.Factions)
            {
                var factionKey = aioFac.Faction.FormKey;
                if (!masterFactionsByKey.TryGetValue(factionKey, out var masterFac)) continue;
                if (!winnerFactionsByKey.TryGetValue(factionKey, out var winnerFac)) continue;

                if (!ShouldForwardAioChange(masterFac.Rank, aioFac.Rank, winnerFac.Rank)) continue;

                var patchFac = patchNpc.Factions.FirstOrDefault(f => f.Faction.FormKey == factionKey);
                if (patchFac == null || patchFac.Rank == aioFac.Rank) continue;
                patchFac.Rank = aioFac.Rank;
                change = true;
            }

            return change;
        }

        private static IList<IFormLinkGetter<ISpellGetter>> GetNpcSpells(INpcGetter npc)
            => GetNpcSpellList(npc);

        private static IList<IFormLinkGetter<ISpellGetter>> GetPatchSpells(Npc npc)
            => GetNpcSpellList(npc);

        internal static List<FormKey> GetNpcSpellFormKeys(INpcGetter npc)
            => GetNpcSpells(npc).Select(s => s.FormKey).ToList();

        internal static List<FormKey> GetNpcSpellFormKeys(Npc npc)
            => GetPatchSpells(npc).Select(s => s.FormKey).ToList();

        private static IList<IFormLinkGetter<ISpellGetter>> GetNpcSpellList(object npc)
        {
            var found = TryGetNpcSpellList(npc);
            if (found != null) return found;
            throw new InvalidOperationException($"Could not find Spells list on {npc.GetType().Name}");
        }

        private static IList<IFormLinkGetter<ISpellGetter>>? TryGetNpcSpellList(object npc)
        {
            foreach (var type in npc.GetType().GetInterfaces().Prepend(npc.GetType()))
            {
                foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (prop.GetIndexParameters().Length > 0) continue;
                    if (!prop.Name.Contains("Spell", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(prop.Name, "LockList", StringComparison.Ordinal))
                        continue;

                    var value = prop.GetValue(npc);
                    if (value is IList<IFormLinkGetter<ISpellGetter>> typed)
                        return typed;
                    if (value is System.Collections.IList list)
                        return new SpellListAdapter(list);
                }
            }

            return null;
        }

        internal static bool TryGetNpcSpellFormKeys(INpcGetter npc, out List<FormKey> spellKeys)
        {
            spellKeys = new List<FormKey>();
            var spells = TryGetNpcSpellList(npc);
            if (spells == null) return false;
            spellKeys = spells.Select(s => s.FormKey).ToList();
            return true;
        }

        private sealed class SpellListAdapter : IList<IFormLinkGetter<ISpellGetter>>
        {
            private readonly System.Collections.IList _inner;
            public SpellListAdapter(System.Collections.IList inner) => _inner = inner;
            public IFormLinkGetter<ISpellGetter> this[int index]
            {
                get => (IFormLinkGetter<ISpellGetter>)_inner[index]!;
                set => _inner[index] = value;
            }
            public int Count => _inner.Count;
            public bool IsReadOnly => _inner.IsReadOnly;
            public void Add(IFormLinkGetter<ISpellGetter> item) => _inner.Add(item);
            public void Clear() => _inner.Clear();
            public bool Contains(IFormLinkGetter<ISpellGetter> item) => _inner.Contains(item);
            public void CopyTo(IFormLinkGetter<ISpellGetter>[] array, int arrayIndex) => _inner.CopyTo(array, arrayIndex);
            public IEnumerator<IFormLinkGetter<ISpellGetter>> GetEnumerator()
                => _inner.Cast<IFormLinkGetter<ISpellGetter>>().GetEnumerator();
            public int IndexOf(IFormLinkGetter<ISpellGetter> item) => _inner.IndexOf(item);
            public void Insert(int index, IFormLinkGetter<ISpellGetter> item) => _inner.Insert(index, item);
            public bool Remove(IFormLinkGetter<ISpellGetter> item)
            {
                var idx = IndexOf(item);
                if (idx < 0) return false;
                _inner.RemoveAt(idx);
                return true;
            }
            public void RemoveAt(int index) => _inner.RemoveAt(index);
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _inner.GetEnumerator();
        }

        public static bool ForwardAioSpellChanges(
            Npc patchNpc,
            ILinkCache linkCache,
            INpcGetter winningMaster,
            INpcGetter aioNpc,
            INpcGetter winningOverride)
        {
            if (TryGetNpcSpellList(winningMaster) == null
                || TryGetNpcSpellList(aioNpc) == null
                || TryGetNpcSpellList(patchNpc) == null)
                return false;

            bool change = false;
            var editorIdCache = new Dictionary<FormKey, string?>();
            var masterSpells = GetNpcSpells(winningMaster);
            var aioSpells = GetNpcSpells(aioNpc);
            var winnerSpells = GetNpcSpells(winningOverride);
            var patchSpells = GetPatchSpells(patchNpc);

            var masterSpellKeys = masterSpells.Select(s => s.FormKey).ToHashSet();
            var aioSpellKeys = aioSpells.Select(s => s.FormKey).ToHashSet();
            var winnerSpellKeys = winnerSpells.Select(s => s.FormKey).ToHashSet();

            var masterSpellsByEditorId = masterSpells
                .Select(s => s.FormKey)
                .Select(k => (Key: k, EditorId: GetSpellEditorId(linkCache, k, editorIdCache)))
                .Where(x => x.EditorId != null)
                .ToDictionary(x => x.EditorId!, x => x.Key, StringComparer.OrdinalIgnoreCase);
            var aioSpellsByEditorId = aioSpells
                .Select(s => s.FormKey)
                .Select(k => (Key: k, EditorId: GetSpellEditorId(linkCache, k, editorIdCache)))
                .Where(x => x.EditorId != null)
                .ToDictionary(x => x.EditorId!, x => x.Key, StringComparer.OrdinalIgnoreCase);
            var winnerSpellsByEditorId = winnerSpells
                .Select(s => s.FormKey)
                .Select(k => (Key: k, EditorId: GetSpellEditorId(linkCache, k, editorIdCache)))
                .Where(x => x.EditorId != null)
                .ToDictionary(x => x.EditorId!, x => x.Key, StringComparer.OrdinalIgnoreCase);

            foreach (var masterKey in masterSpellKeys)
            {
                if (aioSpellKeys.Contains(masterKey)) continue;
                var existing = patchSpells.FirstOrDefault(s => s.FormKey == masterKey);
                if (existing == null) continue;
                patchSpells.Remove(existing);
                change = true;
            }

            foreach (var aioKey in aioSpellKeys)
            {
                if (masterSpellKeys.Contains(aioKey)) continue;
                var editorId = GetSpellEditorId(linkCache, aioKey, editorIdCache);
                if (editorId != null
                    && masterSpellsByEditorId.ContainsKey(editorId)
                    && !winnerSpellsByEditorId.ContainsKey(editorId))
                    continue;

                if (patchSpells.Any(s => s.FormKey == aioKey)) continue;
                patchSpells.Add(new FormLink<ISpellGetter>(aioKey));
                change = true;
            }

            for (int i = 0; i < patchSpells.Count; i++)
            {
                var patchKey = patchSpells[i].FormKey;
                var editorId = GetSpellEditorId(linkCache, patchKey, editorIdCache);
                if (editorId == null) continue;
                if (!masterSpellsByEditorId.TryGetValue(editorId, out var masterKey)) continue;
                if (!aioSpellsByEditorId.TryGetValue(editorId, out var aioKey)) continue;
                if (!winnerSpellsByEditorId.TryGetValue(editorId, out var winnerKey)) continue;

                if (!ShouldForwardAioChange(masterKey, aioKey, winnerKey)) continue;
                if (patchKey == aioKey) continue;
                patchSpells[i] = new FormLink<ISpellGetter>(aioKey);
                change = true;
            }

            return change;
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
