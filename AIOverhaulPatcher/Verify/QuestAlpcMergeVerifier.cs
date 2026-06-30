using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AIOverhaulPatcher.Utilities;
using Mutagen.Bethesda;
using Mutagen.Bethesda.FormKeys.SkyrimSE;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace AIOverhaulPatcher.Verify
{
    internal static class QuestAlpcMergeVerifier
    {
        private const string JojModsRoot = @"D:\Modding\SSE\JOJ\mods";
        private const string StockGameData = @"D:\Modding\SSE\JOJ\Stock Game\Data";
        private const uint Mg01FaraldaAliasId = 11;

        private static readonly FormKey AioWinterholdFaraldaCastLight = new(
            ModKey.FromFileName("AI Overhaul.esp"), 0x345D8F62);

        public static int Run()
        {
            var modPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Skyrim.esm"] = Path.Combine(StockGameData, "Skyrim.esm"),
                ["AI Overhaul.esp"] = Path.Combine(JojModsRoot, "AI Overhaul SSE", "AI Overhaul.esp"),
                ["College Of Winterhold.esp"] = Path.Combine(JojModsRoot, "College Of Winterhold - Quest Expansion", "College Of Winterhold.esp"),
            };

            int failures = 0;
            Console.WriteLine("=== Quest ALPC merge verification ===");
            Console.WriteLine();

            failures += VerifyMg01Alias11(modPaths);

            Console.WriteLine();
            Console.WriteLine(failures == 0
                ? "All verification checks passed."
                : $"Verification finished with {failures} failure(s).");
            return failures == 0 ? 0 : 1;
        }

        private static int VerifyMg01Alias11(Dictionary<string, string> modPaths)
        {
            Console.WriteLine($"--- MG01 alias {Mg01FaraldaAliasId} (Faralda) ---");

            if (!TryGetQuest(modPaths, "Skyrim.esm", Skyrim.Quest.MG01.FormKey, out var master))
            {
                Console.WriteLine("FAIL: master MG01 not found in Skyrim.esm");
                return 1;
            }

            if (!TryGetQuest(modPaths, "AI Overhaul.esp", Skyrim.Quest.MG01.FormKey, out var aio))
            {
                Console.WriteLine("FAIL: AIO MG01 not found in AI Overhaul.esp");
                return 1;
            }

            if (!TryGetQuest(modPaths, "College Of Winterhold.esp", Skyrim.Quest.MG01.FormKey, out var college))
            {
                Console.WriteLine("WARN: College MG01 not found; using master as winning override");
                college = master;
            }

            var masterAlias = AioQuestAlpcUtilities.TryGetReferenceAliasGetter(master, Mg01FaraldaAliasId);
            var aioAlias = AioQuestAlpcUtilities.TryGetReferenceAliasGetter(aio, Mg01FaraldaAliasId);
            var collegeAlias = AioQuestAlpcUtilities.TryGetReferenceAliasGetter(college, Mg01FaraldaAliasId);

            if (masterAlias == null || aioAlias == null || collegeAlias == null)
            {
                Console.WriteLine("FAIL: alias 11 missing on one or more quest records");
                return 1;
            }

            var masterPackages = masterAlias.PackageData.Select(p => p.FormKey).ToList();
            var aioPackages = aioAlias.PackageData.Select(p => p.FormKey).ToList();
            var collegePackages = collegeAlias.PackageData.Select(p => p.FormKey).ToList();

            Console.WriteLine($"  master ALPC ({masterPackages.Count}): {AioPluginUtilities.FormatPackageList(masterPackages)}");
            Console.WriteLine($"  aio ALPC ({aioPackages.Count}): {AioPluginUtilities.FormatPackageList(aioPackages)}");
            Console.WriteLine($"  college ALPC ({collegePackages.Count}): {AioPluginUtilities.FormatPackageList(collegePackages)}");

            if (AioPluginUtilities.PackageListsEqual(masterPackages, aioPackages))
            {
                Console.WriteLine("FAIL: expected AIO to change alias 11 ALPC vs master");
                return 1;
            }

            if (!aioPackages.Contains(AioWinterholdFaraldaCastLight))
            {
                Console.WriteLine($"FAIL: AIO alias 11 missing {AioWinterholdFaraldaCastLight}");
                return 1;
            }

            var merged = AioPluginUtilities.BuildMergedPackageList(
                aioPackages,
                collegePackages,
                new[] { masterPackages },
                Array.Empty<IReadOnlyList<FormKey>>());

            Console.WriteLine($"  merged ALPC ({merged.Count}): {AioPluginUtilities.FormatPackageList(merged)}");

            int failures = 0;
            if (!merged.Contains(AioWinterholdFaraldaCastLight))
            {
                Console.WriteLine($"  FAIL: merged list missing {AioWinterholdFaraldaCastLight}");
                failures++;
            }
            else
            {
                Console.WriteLine($"  PASS: merged list contains AIOWinterholdFaraldaCastLight");
            }

            foreach (var collegePkg in collegePackages)
            {
                if (!merged.Contains(collegePkg))
                {
                    Console.WriteLine($"  FAIL: merged list dropped college package {collegePkg}");
                    failures++;
                }
            }

            if (failures == 0 && collegePackages.Count > 0)
                Console.WriteLine($"  PASS: all {collegePackages.Count} college override package(s) preserved");

            if (merged.Count < collegePackages.Count + 1 && merged.Contains(AioWinterholdFaraldaCastLight))
            {
                Console.WriteLine("  WARN: merged count lower than college+AIO addition; check ordering");
            }

            return failures;
        }

        private static bool TryGetQuest(
            Dictionary<string, string> modPaths,
            string modFile,
            FormKey key,
            out IQuestGetter quest)
        {
            quest = null!;
            if (!modPaths.TryGetValue(modFile, out var path) || !File.Exists(path))
                return false;

            using var mod = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
            if (!mod.Quests.TryGetValue(key, out var found))
                return false;
            quest = found;
            return true;
        }
    }
}
