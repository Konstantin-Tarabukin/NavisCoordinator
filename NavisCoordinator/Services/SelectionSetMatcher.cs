using Autodesk.Navisworks.Api;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace NavisCoordinator.Services
{
    /// <summary>
    /// Matches source Selection Sets exported to JSON against the current Navisworks model.
    /// Dry Run is read-only; the separate transfer mode creates sets only after validation
    /// and explicit confirmation. It never saves the NWF automatically.
    /// The matcher supports category fan-out, identity-verified repeated type nodes,
    /// and conservative structural fan-out for identity-less containers repeated
    /// across Revit levels. Root remapping is configured in rootOverrides.json.
    /// </summary>
    public static class SelectionSetMatcher
    {
        private const int MaxRootSearchDepth = 4;
        private const int MaxIntermediateSearchDepth = 12;
        private const int MaxNodesInspected = 250000;
        private const int MaxPathCandidates = 1000;
        private const int MaxPathSearchVisits = 1000000;
        private const int MaxCandidatePathsInReport = 10;

        public static void RunDryRun()
        {
            RunMatchingWorkflow(false, false);
        }

        // Legacy transfer from an exported Selection Sets JSON. Existing sets are never overwritten.
        public static void RunTransfer()
        {
            RunMatchingWorkflow(true, false);
        }

        // Native rebuild uses an explicit section policy rather than empty/obsolete source selections.
        public static void RunNativeRebuildDryRun()
        {
            RunMatchingWorkflow(false, true);
        }

        public static void RunNativeRebuildTransfer()
        {
            RunMatchingWorkflow(true, true);
        }

        private static void RunMatchingWorkflow(bool applyTransfer, bool nativeRebuild)
        {
            try
            {
                Document document = Autodesk.Navisworks.Api.Application.ActiveDocument;
                if (document == null || document.IsClear)
                {
                    MessageBox.Show(
                        "Сначала откройте целевой NWF с загруженной моделью.",
                        "Navis Coordinator — проверка соответствий",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return;
                }

                if (!applyTransfer && !nativeRebuild)
                {
                    // Temporary diagnostic mode. This uses the existing plugin command,
                    // so no menu/XAML wiring is required. Choose Yes to export the loaded
                    // Navisworks model trees to CSV; choose No for the normal matching Dry Run.
                    DialogResult diagnosticChoice = MessageBox.Show(
                        "Нужен диагностический дамп дерева Navisworks?\n\n" +
                        "Да — сохранить структуру загруженных моделей в CSV и завершить.\n" +
                        "Нет — продолжить обычную проверку Selection Sets.\n" +
                        "Отмена — выйти.",
                        "Navis Coordinator — режим диагностики",
                        MessageBoxButtons.YesNoCancel,
                        MessageBoxIcon.Question);

                    if (diagnosticChoice == DialogResult.Yes)
                    {
                        RunTreeDump(document);
                        return;
                    }

                    if (diagnosticChoice == DialogResult.Cancel)
                        return;

                }

                string sourceJsonPath;
                string sourceJson = nativeRebuild
                    ? ReadNativePolicyJson(out sourceJsonPath)
                    : ReadSourceJson(out sourceJsonPath);
                if (string.IsNullOrEmpty(sourceJson))
                    return;

                JavaScriptSerializer serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = int.MaxValue;
                serializer.RecursionLimit = 100;

                object parsed = serializer.DeserializeObject(sourceJson);
                Dictionary<string, object> sourceDocument = AsDictionary(parsed);
                if (sourceDocument == null)
                {
                    MessageBox.Show(
                        "Выбранный JSON не содержит ожидаемый объект верхнего уровня.",
                        "Navis Coordinator",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return;
                }

                Dictionary<string, object> selectedPolicyTarget = nativeRebuild
                    ? AsDictionary(GetDictionaryValue(sourceDocument, "target"))
                    : null;
                bool universalNativeRebuild = nativeRebuild &&
                    GetDictionaryBoolean(selectedPolicyTarget, "autoDiscoverLinkedRoots", false);

                List<ExpectedSet> expectedSets;
                try
                {
                    expectedSets = nativeRebuild
                        ? (universalNativeRebuild
                            ? ReadUniversalNativeRebuildPolicyExpectedSets(sourceDocument, document, document.Title ?? "")
                            : ReadNativeRebuildPolicyExpectedSets(sourceDocument, document.Title ?? ""))
                        : ReadExpectedSets(sourceDocument);
                }
                catch (Exception policyException)
                {
                    MessageBox.Show(
                        "Не удалось прочитать политику нативного построения Selection Sets:\n\n" +
                        policyException.Message +
                        "\n\nДокумент не изменён.",
                        "Navis Coordinator — ошибка политики",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                    return;
                }

                int sourceSelectionSetCount = expectedSets.Count;
                int sourceExpectedItemCount = CountExpectedItems(expectedSets);
                int expectedItemCount = sourceExpectedItemCount;
                int autoGeneratedRootSetCount = 0;
                List<object> autoGeneratedRootSetReports = new List<object>();
                if (expectedSets.Count == 0 || expectedItemCount == 0)
                {
                    MessageBox.Show(
                        nativeRebuild
                            ? "В политике не найдены разделы или элементы для построения Selection Sets.\nПроверьте выбранный файл профиля разделов."
                            : "В JSON не найдены Selection Sets или выбранные узлы.\nПроверьте, что это файл экспорта формата 2.",
                        "Navis Coordinator",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return;
                }

                string rootOverridesPath = Path.Combine(
                    Path.GetDirectoryName(sourceJsonPath) ?? "",
                    "rootOverrides.json");
                bool rootOverridesConfigExists = File.Exists(rootOverridesPath);
                Dictionary<string, string> rootOverrides;
                try
                {
                    rootOverrides = LoadRootOverrides(rootOverridesPath, serializer);
                    if (nativeRebuild)
                    {
                        string nativePolicyMappingError = ValidateNativePolicyRootOverrides(
                            sourceDocument,
                            rootOverrides,
                            rootOverridesConfigExists);
                        if (!string.IsNullOrWhiteSpace(nativePolicyMappingError))
                        {
                            MessageBox.Show(
                                nativePolicyMappingError + "\n\nДокумент не изменён.",
                                "Navis Coordinator — проверка rootOverrides",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Error);
                            return;
                        }
                    }
                }
                catch (Exception configException)
                {
                    MessageBox.Show(
                        "Не удалось прочитать файл сопоставления моделей:\n\n" +
                        rootOverridesPath + "\n\n" + configException.Message +
                        "\n\nИсправьте rootOverrides.json или временно уберите его из папки исходного JSON.",
                        "Navis Coordinator — ошибка root mapping",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                    return;
                }

                List<object> setReports = new List<object>();
                Dictionary<ExpectedSet, Dictionary<string, ModelItem>> resolvedTargetItemsBySet =
                    new Dictionary<ExpectedSet, Dictionary<string, ModelItem>>();
                int uniqueMatches = 0;
                int oneToManyCategoryMatches = 0;
                int oneToManyIdentityMatches = 0;
                int oneToManyStructuralMatches = 0;
                int oneToManyNativePolicyMatches = 0;
                int identityDisambiguatedMatches = 0;
                int missingItems = 0;
                int ambiguousItems = 0;
                int searchLimitItems = 0;
                int identityWarningCount = 0;
                int nodesInspected = 0;
                int targetNodesIndexed = 0;
                int targetPathSearchVisits = 0;
                bool searchLimitReached = false;

                // Locate each linked-model root once, then index only required branches.
                // The dictionary key stays the SOURCE root name so source paths remain
                // unchanged. Optional rootOverrides.json can map a source RVT filename
                // to another target RVT filename; Navisworks position suffixes are ignored.
                List<string> requiredRootNames = GetRequiredRootNames(expectedSets);
                Dictionary<string, List<TargetNodeInfo>> indexedRootsByName =
                    new Dictionary<string, List<TargetNodeInfo>>(StringComparer.Ordinal);
                HashSet<string> processedRootNames =
                    new HashSet<string>(StringComparer.Ordinal);
                Dictionary<TargetNodeInfo, Dictionary<string, List<TargetNodeInfo>>> descendantMatchCache =
                    new Dictionary<TargetNodeInfo, Dictionary<string, List<TargetNodeInfo>>>();
                List<object> rootMappingReports = new List<object>();

                foreach (string rootName in requiredRootNames)
                {
                    try
                    {
                        string mappedTargetFileName = GetMappedTargetRootFileName(rootName, rootOverrides);
                        bool specialMappingApplied = !string.IsNullOrEmpty(mappedTargetFileName);
                        string targetModelFileName = specialMappingApplied
                            ? mappedTargetFileName
                            : ExtractModelFileName(rootName);

                        List<ModelItem> rootItems;
                        string lookupMode;

                        // Match by RVT filename for every linked model. Navisworks can
                        // change the position suffix (for example, : 7 : -> : 17 :).
                        // rootOverrides is reserved for genuine model renames/remapping.
                        if (!string.IsNullOrWhiteSpace(targetModelFileName))
                        {
                            rootItems = FindStartingNodesByModelFileName(
                                document,
                                targetModelFileName,
                                ref nodesInspected);
                            lookupMode = specialMappingApplied
                                ? "configured-rvt-file-name"
                                : "rvt-file-name";
                        }
                        else
                        {
                            rootItems = FindStartingNodes(
                                document,
                                rootName,
                                ref nodesInspected);
                            lookupMode = "exact-display-name-fallback";
                        }

                        List<TargetNodeInfo> indexedRoots = new List<TargetNodeInfo>();
                        foreach (ModelItem rootItem in rootItems)
                        {
                            TargetNodeInfo indexedRoot = CreateTargetRoot(
                                rootItem,
                                ref nodesInspected,
                                ref targetNodesIndexed);
                            if (indexedRoot != null)
                                indexedRoots.Add(indexedRoot);
                        }

                        indexedRootsByName[rootName] = indexedRoots;
                        processedRootNames.Add(rootName);

                        Dictionary<string, object> rootMappingReport =
                            new Dictionary<string, object>();
                        rootMappingReport["sourceRootName"] = rootName;
                        rootMappingReport["mappingApplied"] = specialMappingApplied;
                        rootMappingReport["lookupMode"] = lookupMode;
                        rootMappingReport["targetModelFileName"] = targetModelFileName;

                        List<string> actualTargetRootNames = new List<string>();
                        foreach (ModelItem rootItem in rootItems)
                            actualTargetRootNames.Add(SafeDisplayName(rootItem));
                        rootMappingReport["targetRootNamesFound"] = actualTargetRootNames;
                        rootMappingReport["targetRootCount"] = rootItems.Count;
                        rootMappingReport["mappingStatus"] = rootItems.Count == 0
                            ? "TARGET_ROOT_NOT_FOUND"
                            : (rootItems.Count == 1 ? "FOUND" : "MULTIPLE_TARGET_ROOTS_FOUND");
                        rootMappingReports.Add(rootMappingReport);
                    }
                    catch (SearchLimitException)
                    {
                        searchLimitReached = true;
                        break;
                    }
                }

                // Native rule-based sections are expanded from the ACTUAL target tree only
                // after every required linked-model root has been located. This keeps the
                // policy stable across P1/P2/P3 while allowing level-specific categories and
                // pipe types to vary naturally between stages.
                List<object> nativeRuleExpansionReports = new List<object>();
                int nativeRuleBasedSetCount = 0;
                if (nativeRebuild)
                {
                    nativeRuleBasedSetCount = CountNativePolicySetsByMode(expectedSets, "ruleBased");
                    bool nativeRuleSearchLimitReached;
                    nativeRuleExpansionReports = ExpandNativeRuleBasedSets(
                        expectedSets,
                        indexedRootsByName,
                        document.Title ?? "",
                        ref nodesInspected,
                        out nativeRuleSearchLimitReached);

                    if (nativeRuleSearchLimitReached)
                        searchLimitReached = true;

                    // A rule-based section initially contains only a linked-root placeholder
                    // for root mapping. Replace that placeholder with the actual policy items
                    // found in the target tree before computing expected counts or matching.
                    sourceExpectedItemCount = CountExpectedItems(expectedSets);
                    expectedItemCount = sourceExpectedItemCount;
                }

                // Additional target links may appear after a model split (for example,
                // the former АСКУЭР link became АСКУЭ and АСКУВТ). They are not source
                // Selection Sets, so create whole-link sets for target roots that remain
                // unmapped, but only after every source root has a unique target.
                bool allSourceRootsMappedUniquely = !searchLimitReached;
                HashSet<string> mappedTargetRootDisplayNames =
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (string sourceRootName in requiredRootNames)
                {
                    List<TargetNodeInfo> sourceRootMatches;
                    if (!indexedRootsByName.TryGetValue(sourceRootName, out sourceRootMatches) ||
                        sourceRootMatches == null || sourceRootMatches.Count != 1)
                    {
                        allSourceRootsMappedUniquely = false;
                        continue;
                    }

                    string mappedDisplayName = sourceRootMatches[0].DisplayName;
                    if (!string.IsNullOrWhiteSpace(mappedDisplayName))
                        mappedTargetRootDisplayNames.Add(mappedDisplayName);
                }

                List<ModelItem> allTargetLinkedRoots = new List<ModelItem>();
                string targetDocumentTitle = document.Title ?? "";
                string targetProjectPrefix = "";
                string targetStageToken = "";
                bool targetProjectStageKnown = universalNativeRebuild || TryGetProjectAndStageToken(
                    targetDocumentTitle,
                    out targetProjectPrefix,
                    out targetStageToken);
                string universalProjectPrefix = universalNativeRebuild
                    ? GetDictionaryString(sourceDocument, "universalProjectPrefix")
                    : "";

                if (allSourceRootsMappedUniquely && targetProjectStageKnown)
                {
                    allTargetLinkedRoots = FindAllLinkedModelRoots(document, ref nodesInspected);
                    if (allTargetLinkedRoots.Count == 0)
                    {
                        Dictionary<string, object> blockingReport = new Dictionary<string, object>();
                        blockingReport["sourceRootName"] = "Инвентаризация связанных моделей";
                        blockingReport["mappingApplied"] = false;
                        blockingReport["lookupMode"] = "LcRevitLink-root-inventory";
                        blockingReport["targetModelFileName"] = "";
                        blockingReport["targetRootNamesFound"] = new List<string>();
                        blockingReport["targetRootCount"] = 0;
                        blockingReport["mappingStatus"] = "TARGET_ROOT_INVENTORY_EMPTY";
                        blockingReport["generatedFromUnmappedTargetRoot"] = true;
                        rootMappingReports.Add(blockingReport);
                    }

                    if (!nativeRebuild)
                    {
                        HashSet<string> usedSyntheticSetNames =
                            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (ExpectedSet existingSet in expectedSets)
                        {
                            if (existingSet != null && !string.IsNullOrWhiteSpace(existingSet.Name))
                                usedSyntheticSetNames.Add(existingSet.Name);
                        }

                        foreach (ModelItem targetRootItem in allTargetLinkedRoots)
                        {
                            string targetRootDisplayName = SafeDisplayName(targetRootItem);
                            string targetRootFileName = ExtractModelFileName(targetRootDisplayName);
                            if (string.IsNullOrWhiteSpace(targetRootFileName))
                                continue;

                            if (mappedTargetRootDisplayNames.Contains(targetRootDisplayName))
                                continue;

                            string targetRootProjectPrefix;
                            string targetRootStageToken;
                            if (!TryGetProjectAndStageToken(
                                targetRootFileName,
                                out targetRootProjectPrefix,
                                out targetRootStageToken))
                            {
                                continue;
                            }

                            // Ignore unrelated linked files from other projects/stages.
                            if (!string.Equals(targetProjectPrefix, targetRootProjectPrefix, StringComparison.OrdinalIgnoreCase) ||
                                !string.Equals(targetStageToken, targetRootStageToken, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            string sectionCode = ExtractModelSectionCode(targetRootFileName);
                            string generatedSetName = TransliterateSectionCodeToSetName(sectionCode);
                            Dictionary<string, object> generatedRootReport =
                                new Dictionary<string, object>();
                            generatedRootReport["targetRootName"] = targetRootDisplayName;
                            generatedRootReport["targetModelFileName"] = targetRootFileName;
                            generatedRootReport["sectionCode"] = sectionCode;
                            generatedRootReport["generatedSelectionSetName"] = generatedSetName;

                            if (string.IsNullOrWhiteSpace(generatedSetName))
                            {
                                generatedRootReport["status"] = "BLOCKED_NAME_COULD_NOT_BE_DERIVED";
                                autoGeneratedRootSetReports.Add(generatedRootReport);
                                rootMappingReports.Add(BuildGeneratedRootMappingReport(
                                    targetRootDisplayName,
                                    targetRootFileName,
                                    generatedSetName,
                                    "AUTO_SET_NAME_NOT_DERIVED",
                                    1));
                                continue;
                            }

                            if (!usedSyntheticSetNames.Add(generatedSetName))
                            {
                                generatedRootReport["status"] = "BLOCKED_DUPLICATE_GENERATED_SET_NAME";
                                autoGeneratedRootSetReports.Add(generatedRootReport);
                                rootMappingReports.Add(BuildGeneratedRootMappingReport(
                                    targetRootDisplayName,
                                    targetRootFileName,
                                    generatedSetName,
                                    "AUTO_SET_NAME_COLLISION",
                                    1));
                                continue;
                            }

                            TargetNodeInfo indexedUnmappedRoot = CreateTargetRoot(
                                targetRootItem,
                                ref nodesInspected,
                                ref targetNodesIndexed);
                            if (indexedUnmappedRoot == null)
                                continue;

                            ExpectedSet generatedSet = CreateWholeRootExpectedSet(
                                document,
                                targetRootDisplayName,
                                targetRootFileName,
                                generatedSetName);

                            if (indexedRootsByName.ContainsKey(targetRootDisplayName))
                            {
                                generatedRootReport["status"] = "BLOCKED_ROOT_KEY_COLLISION";
                                autoGeneratedRootSetReports.Add(generatedRootReport);
                                rootMappingReports.Add(BuildGeneratedRootMappingReport(
                                    targetRootDisplayName,
                                    targetRootFileName,
                                    generatedSetName,
                                    "AUTO_ROOT_KEY_COLLISION",
                                    1));
                                continue;
                            }

                            indexedRootsByName.Add(
                                targetRootDisplayName,
                                new List<TargetNodeInfo> { indexedUnmappedRoot });
                            processedRootNames.Add(targetRootDisplayName);
                            expectedSets.Add(generatedSet);
                            expectedItemCount++;
                            autoGeneratedRootSetCount++;

                            generatedRootReport["status"] = "AUTO_SET_READY";
                            autoGeneratedRootSetReports.Add(generatedRootReport);
                            rootMappingReports.Add(BuildGeneratedRootMappingReport(
                                targetRootDisplayName,
                                targetRootFileName,
                                generatedSetName,
                                "FOUND",
                                1));
                        }
                    }
                    else
                    {
                        // Native policy is the source of truth. Never silently create a
                        // whole-link set for an unknown section; report it and let preflight
                        // block until the policy is deliberately updated.
                        foreach (ModelItem targetRootItem in allTargetLinkedRoots)
                        {
                            string targetRootDisplayName = SafeDisplayName(targetRootItem);
                            if (mappedTargetRootDisplayNames.Contains(targetRootDisplayName))
                                continue;

                            string targetRootFileName = ExtractModelFileName(targetRootDisplayName);
                            if (string.IsNullOrWhiteSpace(targetRootFileName))
                                continue;

                            if (universalNativeRebuild)
                            {
                                string rootProjectPrefix = ExtractProjectPrefixBeforeRdMarker(targetRootFileName);
                                if (string.IsNullOrWhiteSpace(rootProjectPrefix))
                                    continue;
                                if (!string.IsNullOrWhiteSpace(universalProjectPrefix) &&
                                    !string.Equals(universalProjectPrefix, rootProjectPrefix, StringComparison.OrdinalIgnoreCase))
                                    continue;
                            }
                            else
                            {
                                string extraProjectPrefix;
                                string extraStageToken;
                                if (!TryGetProjectAndStageToken(targetRootFileName, out extraProjectPrefix, out extraStageToken) ||
                                    !string.Equals(targetProjectPrefix, extraProjectPrefix, StringComparison.OrdinalIgnoreCase) ||
                                    !string.Equals(targetStageToken, extraStageToken, StringComparison.OrdinalIgnoreCase))
                                    continue;
                            }

                            Dictionary<string, object> unmappedReport = new Dictionary<string, object>();
                            unmappedReport["targetRootName"] = targetRootDisplayName;
                            unmappedReport["targetModelFileName"] = targetRootFileName;
                            unmappedReport["status"] = "NATIVE_POLICY_REQUIRED_UNMAPPED_TARGET_ROOT";
                            autoGeneratedRootSetReports.Add(unmappedReport);
                            rootMappingReports.Add(BuildGeneratedRootMappingReport(
                                targetRootDisplayName,
                                targetRootFileName,
                                "",
                                "UNMAPPED_TARGET_ROOT_NOT_IN_NATIVE_POLICY",
                                1));
                        }
                    }
                }
                else
                {
                    Dictionary<string, object> discoveryReport = new Dictionary<string, object>();
                    discoveryReport["status"] = allSourceRootsMappedUniquely
                        ? "SKIPPED_TARGET_PROJECT_STAGE_UNAVAILABLE"
                        : "SKIPPED_SOURCE_ROOT_MAPPING_NOT_UNIQUE";
                    discoveryReport["targetDocumentTitle"] = targetDocumentTitle;
                    autoGeneratedRootSetReports.Add(discoveryReport);

                    // If all source roots mapped but the target project/stage cannot be
                    // determined, do not silently skip potentially required new roots.
                    if (allSourceRootsMappedUniquely && !targetProjectStageKnown)
                    {
                        Dictionary<string, object> blockingReport = new Dictionary<string, object>();
                        blockingReport["sourceRootName"] = "Автообнаружение новых корней моделей";
                        blockingReport["mappingApplied"] = false;
                        blockingReport["lookupMode"] = "target-project-stage-validation";
                        blockingReport["targetModelFileName"] = "";
                        blockingReport["targetRootNamesFound"] = new List<string>();
                        blockingReport["targetRootCount"] = 0;
                        blockingReport["mappingStatus"] = "TARGET_PROJECT_STAGE_UNAVAILABLE";
                        blockingReport["generatedFromUnmappedTargetRoot"] = true;
                        rootMappingReports.Add(blockingReport);
                    }
                }

                foreach (ExpectedSet expectedSet in expectedSets)
                {
                    List<object> itemReports = new List<object>();
                    int setUniqueMatches = 0;
                    int setOneToManyCategoryMatches = 0;
                    int setOneToManyIdentityMatches = 0;
                    int setOneToManyStructuralMatches = 0;
                    int setOneToManyNativePolicyMatches = 0;
                    int setIdentityDisambiguatedMatches = 0;
                    int setMissing = 0;
                    int setAmbiguous = 0;
                    int setSearchLimit = 0;
                    int setIdentityWarnings = 0;
                    Dictionary<string, ModelItem> targetItemsByPath =
                        new Dictionary<string, ModelItem>(StringComparer.Ordinal);

                    foreach (ExpectedItem expectedItem in expectedSet.Items)
                    {
                        List<string> expectedPathSegments = GetSourcePathSegments(expectedItem.HierarchyNodes);
                        List<List<TargetNodeInfo>> candidatePaths = new List<List<TargetNodeInfo>>();
                        string status = "MISSING";
                        string targetPath = "";
                        List<string> identityWarnings = new List<string>();
                        int foundCount = 0;
                        int candidateCountBeforeIdentityResolution = 0;
                        bool identityDisambiguated = false;
                        bool oneToManyMatch = false;
                        string decisionReason = "NOT_EVALUATED";
                        List<string> targetHierarchyPaths = new List<string>();

                        if (expectedPathSegments.Count == 0)
                        {
                            status = "MISSING";
                            decisionReason = "INVALID_OR_EMPTY_SOURCE_HIERARCHY_PATH";
                        }
                        else if (searchLimitReached || !processedRootNames.Contains(expectedPathSegments[0]))
                        {
                            // Once a global safety limit has been hit, do not start another
                            // expensive search for each remaining source item. Their status is
                            // explicitly inconclusive, not MISSING.
                            status = "SEARCH_LIMIT_REACHED";
                            decisionReason = "GLOBAL_SEARCH_LIMIT_REACHED_OR_ROOT_NOT_PROCESSED";
                            searchLimitReached = true;
                        }
                        else
                        {
                            try
                            {
                                candidatePaths = FindPathsByHierarchy(
                                    indexedRootsByName,
                                    expectedPathSegments,
                                    descendantMatchCache,
                                    ref nodesInspected,
                                    ref targetNodesIndexed,
                                    ref targetPathSearchVisits);
                                foundCount = candidatePaths.Count;
                                candidateCountBeforeIdentityResolution = foundCount;

                                // Identity values may disambiguate ordinary selected items.
                                // Never use this to collapse a selected category into one branch.
                                if (foundCount > 1 && !IsExpectedTerminalCategory(expectedItem))
                                {
                                    List<List<TargetNodeInfo>> identityResolvedPaths =
                                        ResolveCandidatePathsByIdentity(expectedItem, candidatePaths);

                                    if (identityResolvedPaths.Count == 1 && identityResolvedPaths.Count < foundCount)
                                    {
                                        candidatePaths = identityResolvedPaths;
                                        foundCount = candidatePaths.Count;
                                        identityDisambiguated = true;
                                    }
                                }
                            }
                            catch (SearchLimitException)
                            {
                                searchLimitReached = true;
                                status = "SEARCH_LIMIT_REACHED";
                                decisionReason = "PATH_SEARCH_LIMIT_REACHED";
                            }
                        }

                        if (status != "SEARCH_LIMIT_REACHED")
                        {
                            if (foundCount == 0)
                            {
                                status = "MISSING";
                                decisionReason = expectedPathSegments.Count == 0
                                    ? "INVALID_OR_EMPTY_SOURCE_HIERARCHY_PATH"
                                    : "NO_CANDIDATE_PATH";
                                setMissing++;
                                missingItems++;
                            }
                            else if (foundCount > 1)
                            {
                                string fanOutReason;
                                if (IsExpectedTerminalCategory(expectedItem) &&
                                    AllCandidatePathsTerminateInCategories(candidatePaths) &&
                                    AllCandidatePathsShareTargetRoot(candidatePaths))
                                {
                                    status = "MATCHED_ONE_TO_MANY_CATEGORY";
                                    decisionReason = "SOURCE_AND_TARGET_TERMINALS_ARE_CATEGORIES_IN_ONE_TARGET_ROOT";
                                    oneToManyMatch = true;
                                    setOneToManyCategoryMatches++;
                                    oneToManyCategoryMatches++;
                                    AddTargetHierarchyPaths(candidatePaths, targetHierarchyPaths);
                                }
                                else if (nativeRebuild && TryClassifyNativePolicyRepeatedCandidates(
                                    expectedItem,
                                    candidatePaths,
                                    out fanOutReason))
                                {
                                    status = "MATCHED_ONE_TO_MANY_NATIVE_POLICY";
                                    decisionReason = fanOutReason;
                                    oneToManyMatch = true;
                                    setOneToManyNativePolicyMatches++;
                                    oneToManyNativePolicyMatches++;
                                    AddTargetHierarchyPaths(candidatePaths, targetHierarchyPaths);
                                }
                                else if (TryClassifyRepeatedCandidates(
                                    expectedItem,
                                    candidatePaths,
                                    out status,
                                    out fanOutReason))
                                {
                                    decisionReason = fanOutReason;
                                    oneToManyMatch = true;
                                    AddTargetHierarchyPaths(candidatePaths, targetHierarchyPaths);

                                    if (status == "MATCHED_ONE_TO_MANY_IDENTITY")
                                    {
                                        setOneToManyIdentityMatches++;
                                        oneToManyIdentityMatches++;
                                    }
                                    else if (status == "MATCHED_ONE_TO_MANY_STRUCTURAL")
                                    {
                                        setOneToManyStructuralMatches++;
                                        oneToManyStructuralMatches++;
                                    }
                                }
                                else
                                {
                                    status = "AMBIGUOUS";
                                    decisionReason = fanOutReason;
                                    setAmbiguous++;
                                    ambiguousItems++;
                                }
                            }
                            else
                            {
                                List<ModelItem> targetHierarchyNodes = ConvertToModelItems(candidatePaths[0]);
                                TargetNodeInfo terminalNode = candidatePaths[0][candidatePaths[0].Count - 1];
                                targetPath = BuildActualHierarchyPath(terminalNode);
                                identityWarnings = CompareIdentityProperties(expectedItem, targetHierarchyNodes);

                                setUniqueMatches++;
                                uniqueMatches++;
                                if (identityDisambiguated)
                                {
                                    setIdentityDisambiguatedMatches++;
                                    identityDisambiguatedMatches++;
                                    decisionReason = "IDENTITY_SELECTED_ONE_UNIQUE_PATH";
                                }
                                else
                                {
                                    decisionReason = "ONE_HIERARCHY_PATH_FOUND";
                                }

                                if (identityWarnings.Count > 0)
                                {
                                    status = "MATCHED_WITH_IDENTITY_WARNINGS";
                                    decisionReason = "ONE_PATH_FOUND_BUT_IDENTITY_PROPERTIES_DIFFER_OR_ARE_UNAVAILABLE";
                                    setIdentityWarnings += identityWarnings.Count;
                                    identityWarningCount += identityWarnings.Count;
                                }
                                else
                                {
                                    status = "MATCHED";
                                }
                            }
                        }

                        // Retain only fully accepted matches for the optional transfer phase.
                        // A single source item may contribute multiple target hierarchy nodes.
                        if (IsTransferableItemStatus(status))
                        {
                            foreach (List<TargetNodeInfo> candidatePath in candidatePaths)
                            {
                                if (candidatePath == null || candidatePath.Count == 0)
                                    continue;

                                TargetNodeInfo terminalNode = candidatePath[candidatePath.Count - 1];
                                if (terminalNode == null || terminalNode.Item == null)
                                    continue;

                                string actualPath = BuildActualHierarchyPath(terminalNode);
                                if (!string.IsNullOrWhiteSpace(actualPath) &&
                                    !targetItemsByPath.ContainsKey(actualPath))
                                {
                                    targetItemsByPath.Add(actualPath, terminalNode.Item);
                                }
                            }
                        }

                        if (status == "SEARCH_LIMIT_REACHED")
                        {
                            setSearchLimit++;
                            searchLimitItems++;
                        }

                        Dictionary<string, object> itemReport = new Dictionary<string, object>();
                        itemReport["sourceDisplayName"] = expectedItem.DisplayName;
                        itemReport["sourceHierarchyPath"] = expectedItem.SourceHierarchyPath;
                        itemReport["status"] = status;
                        itemReport["decision"] = status;
                        itemReport["reasonCode"] = decisionReason;
                        itemReport["sourceTerminalNodeType"] = GetExpectedTerminalNodeType(expectedItem);
                        itemReport["sourceIdentityValues"] = BuildSourceIdentityValueReports(expectedItem);
                        itemReport["candidateCount"] = foundCount;
                        itemReport["candidateCountBeforeIdentityResolution"] = candidateCountBeforeIdentityResolution;
                        itemReport["identityDisambiguated"] = identityDisambiguated;
                        itemReport["targetHierarchyPath"] = targetPath;
                        itemReport["targetHierarchyPaths"] = targetHierarchyPaths;
                        itemReport["identityWarnings"] = identityWarnings;

                        if (status == "AMBIGUOUS" || oneToManyMatch)
                        {
                            itemReport["candidatePaths"] = BuildCandidatePathReports(candidatePaths);
                            itemReport["candidatePathsTruncated"] = foundCount > MaxCandidatePathsInReport;
                        }

                        itemReports.Add(itemReport);
                    }

                    resolvedTargetItemsBySet[expectedSet] = targetItemsByPath;

                    Dictionary<string, object> setReport = new Dictionary<string, object>();
                    setReport["name"] = expectedSet.Name;
                    setReport["isNativePolicySet"] = expectedSet.IsNativePolicySet;
                    setReport["nativeSectionCode"] = expectedSet.NativeSectionCode;
                    setReport["nativeMode"] = expectedSet.NativeMode;
                    setReport["nativeSourceModelFileName"] = expectedSet.NativeSourceModelFileName;
                    setReport["nativeExpectedTargetModelFileName"] = expectedSet.NativeExpectedTargetModelFileName;
                    setReport["nativeRuleExpansionStatus"] = expectedSet.NativeRuleExpansionStatus;
                    setReport["nativeRuleExpansionItemCount"] = expectedSet.NativeRuleExpansionItemCount;
                    setReport["generatedFromUnmappedTargetRoot"] = expectedSet.GeneratedFromUnmappedTargetRoot;
                    setReport["generatedTargetModelFileName"] = expectedSet.GeneratedTargetModelFileName;
                    setReport["expectedItemCount"] = expectedSet.Items.Count;
                    setReport["resolvedTargetModelItemCount"] = targetItemsByPath.Count;
                    setReport["uniqueMatchCount"] = setUniqueMatches;
                    setReport["oneToManyCategoryMatchCount"] = setOneToManyCategoryMatches;
                    setReport["oneToManyIdentityMatchCount"] = setOneToManyIdentityMatches;
                    setReport["oneToManyStructuralMatchCount"] = setOneToManyStructuralMatches;
                    setReport["oneToManyNativePolicyMatchCount"] = setOneToManyNativePolicyMatches;
                    setReport["identityDisambiguatedCount"] = setIdentityDisambiguatedMatches;
                    setReport["missingCount"] = setMissing;
                    setReport["ambiguousCount"] = setAmbiguous;
                    setReport["searchLimitCount"] = setSearchLimit;
                    setReport["identityWarningCount"] = setIdentityWarnings;
                    setReport["items"] = itemReports;
                    setReports.Add(setReport);
                }

                bool transferApplied = false;
                bool modelMayBeModified = false;
                string transferStatus = applyTransfer ? "NOT_STARTED" : "NOT_REQUESTED";
                string transferError = "";
                int createdSelectionSetCount = 0;
                int replacedSelectionSetCount = 0;
                int createdTargetModelItemCount = 0;
                Dictionary<string, object> coverageAnalysis = nativeRebuild
                    ? BuildCoverageAnalysis(expectedSets, indexedRootsByName)
                    : null;

                Dictionary<string, object> report = new Dictionary<string, object>();
                report["reportFormatVersion"] = 10;
                report["workflowMode"] = nativeRebuild ? "NATIVE_REBUILD_FROM_SECTION_POLICY" : "LEGACY_JSON_TRANSFER";
                report["nativeRebuildMode"] = nativeRebuild;
                report["sourceDocumentTitle"] = nativeRebuild
                    ? GetDictionaryString(sourceDocument, "title")
                    : GetDictionaryString(sourceDocument, "documentTitle");
                report["sourceExportedAt"] = GetDictionaryString(sourceDocument, "exportedAt");
                report["policySourceStage"] = nativeRebuild
                    ? GetDictionaryString(AsDictionary(GetDictionaryValue(sourceDocument, "policySource")), "stage")
                    : "";
                report["coverageAnalysis"] = coverageAnalysis;
                report["nativeRuleExpansion"] = nativeRebuild ? nativeRuleExpansionReports : null;
                report["nativePolicyPath"] = nativeRebuild ? sourceJsonPath : "";
                report["nativePolicySetCount"] = nativeRebuild ? expectedSets.Count : 0;
                report["nativePolicyWholeLinkSetCount"] = nativeRebuild
                    ? CountNativePolicySetsByMode(expectedSets, "wholeLink") : 0;
                report["nativePolicyExplicitNodeSetCount"] = nativeRebuild
                    ? CountNativePolicySetsByMode(expectedSets, "explicitNodes") : 0;
                report["nativePolicyRuleBasedSetCount"] = nativeRebuild
                    ? CountNativePolicySetsByMode(expectedSets, "ruleBased") : 0;
                report["nativePolicyRuleExpansionEngine"] = nativeRebuild
                    ? (universalNativeRebuild
                        ? "universal-linked-root-discovery-and-target-tree-category-allowlist-v1"
                        : "target-tree-category-allowlist-and-path-rule-expansion-v1")
                    : "";
                report["nativePolicyNewSplitRootSetCount"] = nativeRebuild
                    ? CountNativePolicySetsBySections(expectedSets, new string[] { "АСКУЭ", "АСКУВТ" }) : 0;
                report["targetDocumentTitle"] = document.Title ?? "";
                report["generatedAt"] = DateTime.Now.ToString(
                    "yyyy-MM-dd HH:mm:ss",
                    CultureInfo.InvariantCulture);
                report["searchMethod"] = "indexed-linked-model-trees-with-cached-descendant-matches-identity-disambiguation-and-guarded-repeated-level-fan-out";
                report["searchOptimization"] = "expand-LcRevitLayer-by-ModelItem.ClassName; expand-category-nodes-by-Element.Type; count-node-name-comparisons-once; stop-after-global-search-limit";
                report["rootMappingPolicy"] = universalNativeRebuild
                    ? "Universal mode: discover current-project RVT roots by _РД_ prefix and configured section code; map each root directly by its actual RVT filename; stage and fixed root count are not used."
                    : (rootOverridesConfigExists
                        ? "RVT filename lookup by default; rootOverrides.json maps source RVT filenames to renamed or reassigned target RVT filenames. Navisworks position suffixes are ignored."
                        : "RVT filename lookup by default; rootOverrides.json was not found next to the selected source JSON.");
                report["universalNativeRebuild"] = universalNativeRebuild;
                report["universalProjectPrefix"] = universalNativeRebuild ? universalProjectPrefix : "";
                report["universalDetectedRoots"] = universalNativeRebuild
                    ? GetDictionaryValue(sourceDocument, "universalDetectedRoots") : null;
                report["universalUnsupportedRootsAtExpansion"] = universalNativeRebuild
                    ? GetDictionaryValue(sourceDocument, "universalUnsupportedRootsAtExpansion") : null;
                report["rootOverridesConfigPath"] = rootOverridesPath;
                report["rootOverridesConfigLoaded"] = rootOverridesConfigExists;
                report["rootOverridesCount"] = rootOverrides.Count;
                report["rootMappings"] = rootMappingReports;
                report["sourceSelectionSetCount"] = sourceSelectionSetCount;
                report["sourceExpectedItemCount"] = sourceExpectedItemCount;
                report["autoGeneratedRootSetCount"] = autoGeneratedRootSetCount;
                report["autoGeneratedRootSets"] = autoGeneratedRootSetReports;
                report["targetLinkedRootCount"] = allTargetLinkedRoots.Count;
                report["targetProjectPrefixForAutoSets"] = universalNativeRebuild
                    ? universalProjectPrefix
                    : (targetProjectStageKnown ? targetProjectPrefix : "");
                report["targetStageTokenForAutoSets"] = universalNativeRebuild
                    ? "NOT_USED_IN_UNIVERSAL_MODE"
                    : (targetProjectStageKnown ? targetStageToken : "");
                report["categoryFanOutRule"] = "source-and-target-terminal-nodes-are-categories-and-all-candidates-share-one-target-root";
                report["identityFanOutRule"] = "all-nonempty-source-identity-properties-match-every-candidate; candidates share one target root and repeat across distinct LcRevitLayer nodes with the same canonical path";
                report["structuralFanOutRule"] = "source-and-target nodes have no readable identity values; candidates share one target root and repeat across distinct LcRevitLayer nodes with the same canonical path after level nodes are excluded";
                report["nativePolicyFanOutRule"] = "for native policy Family/Type paths, include all same canonical-path candidates under distinct LcRevitLayer nodes when every candidate has the expected terminal type and all candidates belong to one mapped linked-model root";
                report["targetSetNameHandling"] = nativeRebuild
                    ? "replace exactly-one top-level SelectionSet with canonical name; create if absent; block nested folders, duplicate names, or non-SelectionSet collisions"
                    : "never overwrite an existing saved item with the same name";
                report["maxIntermediateSearchDepth"] = MaxIntermediateSearchDepth;
                report["targetNodesInspected"] = nodesInspected;
                report["targetNodesIndexed"] = targetNodesIndexed;
                report["targetPathSearchVisits"] = targetPathSearchVisits;
                report["maxTargetPathSearchVisits"] = MaxPathSearchVisits;
                report["candidatePathsIncludedForAmbiguousItems"] = MaxCandidatePathsInReport;
                report["selectionSetCount"] = expectedSets.Count;
                report["expectedItemCount"] = expectedItemCount;
                report["uniqueMatchCount"] = uniqueMatches;
                report["oneToManyCategoryMatchCount"] = oneToManyCategoryMatches;
                report["identityDisambiguatedCount"] = identityDisambiguatedMatches;
                report["oneToManyIdentityMatchCount"] = oneToManyIdentityMatches;
                report["oneToManyStructuralMatchCount"] = oneToManyStructuralMatches;
                report["oneToManyNativePolicyMatchCount"] = oneToManyNativePolicyMatches;
                report["resolvedItemCount"] = uniqueMatches + oneToManyCategoryMatches + oneToManyIdentityMatches + oneToManyStructuralMatches + oneToManyNativePolicyMatches;
                report["missingCount"] = missingItems;
                report["ambiguousCount"] = ambiguousItems;
                report["searchLimitItemCount"] = searchLimitItems;
                report["identityWarningCount"] = identityWarningCount;
                report["searchLimitReached"] = searchLimitReached;
                report["transferRequested"] = applyTransfer;
                report["transferStatus"] = transferStatus;
                report["transferError"] = transferError;
                report["createdSelectionSetCount"] = createdSelectionSetCount;
                report["createdTargetModelItemCount"] = createdTargetModelItemCount;
                report["modelWasModified"] = false;
                report["selectionSets"] = setReports;

                using (SaveFileDialog dialog = new SaveFileDialog())
                {
                    dialog.Title = applyTransfer
                        ? "Сохранить отчёт переноса Selection Sets"
                        : "Сохранить отчёт проверки Selection Sets";
                    dialog.Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*";
                    dialog.FileName = (nativeRebuild
                        ? (applyTransfer ? "NavisCoordinator_NativeRebuildReport_" : "NavisCoordinator_NativeRebuildDryRun_")
                        : (applyTransfer ? "NavisCoordinator_TransferReport_" : "NavisCoordinator_MatchReport_")) +
                        DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json";
                    dialog.InitialDirectory = Environment.GetFolderPath(
                        Environment.SpecialFolder.Desktop);

                    // Pick the report path before any document mutation. Canceling this
                    // dialog therefore guarantees that the target document remains untouched.
                    if (dialog.ShowDialog() != DialogResult.OK)
                        return;

                    if (string.Equals(dialog.FileName, sourceJsonPath, StringComparison.OrdinalIgnoreCase))
                    {
                        MessageBox.Show(
                            "Нельзя сохранять отчёт поверх исходного JSON Selection Sets. Выберите другое имя файла.",
                            "Navis Coordinator — неверный путь отчёта",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);
                        return;
                    }

                    if (applyTransfer)
                    {
                        string validationDetails;
                        if (!ValidateTransferPreconditions(
                            document,
                            expectedSets,
                            resolvedTargetItemsBySet,
                            rootMappingReports,
                            nativeRuleExpansionReports,
                            expectedItemCount,
                            uniqueMatches + oneToManyCategoryMatches +
                                oneToManyIdentityMatches + oneToManyStructuralMatches + oneToManyNativePolicyMatches,
                            missingItems,
                            ambiguousItems,
                            searchLimitItems,
                            identityWarningCount,
                            searchLimitReached,
                            nativeRebuild,
                            out validationDetails))
                        {
                            transferStatus = "BLOCKED_PREFLIGHT";
                            transferError = validationDetails;
                            MessageBox.Show(
                                "Перенос не выполнен. Предварительная проверка не пройдена:\n\n" +
                                validationDetails +
                                "\n\nДокумент не изменён. Отчёт будет сохранён.",
                                "Navis Coordinator — перенос заблокирован",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Warning);
                        }
                        else
                        {
                            int targetItemTotal = CountResolvedTargetItems(
                                expectedSets, resolvedTargetItemsBySet);
                            DialogResult transferChoice = MessageBox.Show(
                                "Предварительная проверка пройдена.\n\n" +
                                "Целевой документ: " + (document.Title ?? "") + "\n" +
                                (nativeRebuild ? "Selection Sets для нативного построения: " : "Selection Sets для создания: ") + expectedSets.Count + "\n" +
                                (nativeRebuild
                                    ? "Наборов по политике для новых связей АСКУЭ/АСКУВТ: " + CountNativePolicySetsBySections(expectedSets, new string[] { "АСКУЭ", "АСКУВТ" }) + "\n"
                                    : "Дополнительных наборов для новых связанных моделей: " + autoGeneratedRootSetCount + "\n") +
                                (nativeRebuild
                                    ? "Существующих наборов будет заменено по имени: " + CountReusableTargetSets(document, expectedSets) + "\n"
                                    : "") +
                                "Суммарно уникальных целевых узлов во всех наборах: " + targetItemTotal + "\n\n" +
                                "ВАЖНО: продолжайте только если открыт РАБОЧИЙ КЛОН целевого NWF.\n" +
                                (nativeRebuild
                                    ? (universalNativeRebuild
                                        ? "Универсальный режим построит наборы по профилям разделов, найденным в текущем NWF, заменяя только уникальные одноимённые Selection Sets верхнего уровня и создавая отсутствующие.\n"
                                        : "Нативный режим построит наборы по выбранной политике разделов, заменяя только уникальные одноимённые Selection Sets верхнего уровня и создавая отсутствующие.\n")
                                    : "Существующие наборы с такими именами не перезаписываются.\n") +
                                "Документ будет изменён в памяти; автоматического сохранения на диск не будет.\n\n" +
                                (nativeRebuild ? "Пересобрать Selection Sets по политике?" : "Создать Selection Sets?"),
                                "Navis Coordinator — подтверждение переноса",
                                MessageBoxButtons.YesNo,
                                MessageBoxIcon.Warning);

                            if (transferChoice == DialogResult.Yes)
                            {
                                try
                                {
                                    if (nativeRebuild)
                                    {
                                        CreateOrReplaceNativeSelectionSets(
                                            document,
                                            expectedSets,
                                            resolvedTargetItemsBySet,
                                            out createdSelectionSetCount,
                                            out replacedSelectionSetCount,
                                            out createdTargetModelItemCount);
                                    }
                                    else
                                    {
                                        CreateSelectionSets(
                                            document,
                                            expectedSets,
                                            resolvedTargetItemsBySet,
                                            out createdSelectionSetCount,
                                            out createdTargetModelItemCount);
                                    }
                                    transferApplied = true;
                                    modelMayBeModified = true;
                                    transferStatus = "APPLIED_UNSAVED";
                                    transferError = "";
                                }
                                catch (Exception transferException)
                                {
                                    bool rollbackIncomplete = transferException.Message.IndexOf(
                                        "При попытке отката возникли проблемы",
                                        StringComparison.OrdinalIgnoreCase) >= 0;
                                    transferStatus = rollbackIncomplete
                                        ? "FAILED_ROLLBACK_INCOMPLETE"
                                        : "FAILED_ROLLED_BACK";
                                    modelMayBeModified = rollbackIncomplete;
                                    transferError = transferException.ToString();
                                    MessageBox.Show(
                                        "Во время создания Selection Sets произошла ошибка.\n\n" +
                                        transferException.Message +
                                        "\n\nПлагин попытался откатить все изменения этой операции.\n" +
                                        "Проверьте окно Selection Sets и не сохраняйте документ, пока не убедитесь в результате.\n\n" +
                                        "Полная ошибка записана в JSON-отчёт.",
                                        "Navis Coordinator — ошибка переноса",
                                        MessageBoxButtons.OK,
                                        MessageBoxIcon.Error);
                                }
                            }
                            else
                            {
                                transferStatus = "CANCELLED_BY_USER";
                            }
                        }
                    }

                    report["transferStatus"] = transferStatus;
                    report["transferError"] = transferError;
                    report["createdSelectionSetCount"] = createdSelectionSetCount;
                    report["replacedSelectionSetCount"] = replacedSelectionSetCount;
                    report["createdTargetModelItemCount"] = createdTargetModelItemCount;
                    report["modelWasModified"] = modelMayBeModified;

                    string compactJson = serializer.Serialize(report);
                    string formattedJson = PrettyPrintJson(compactJson);
                    File.WriteAllText(dialog.FileName, formattedJson, new UTF8Encoding(false));

                    ShowSummary(
                        dialog.FileName,
                        nativeRebuild
                            ? GetDictionaryString(sourceDocument, "title")
                            : GetDictionaryString(sourceDocument, "documentTitle"),
                        document.Title ?? "",
                        expectedSets.Count,
                        expectedItemCount,
                        uniqueMatches,
                        oneToManyCategoryMatches,
                        oneToManyIdentityMatches,
                        oneToManyStructuralMatches,
                        oneToManyNativePolicyMatches,
                        identityDisambiguatedMatches,
                        missingItems,
                        ambiguousItems,
                        searchLimitItems,
                        identityWarningCount,
                        nodesInspected,
                        targetNodesIndexed,
                        targetPathSearchVisits,
                        searchLimitReached,
                        applyTransfer,
                        transferApplied,
                        transferStatus,
                        createdSelectionSetCount,
                        replacedSelectionSetCount,
                        createdTargetModelItemCount,
                        transferError);
                }
            }
            catch (Exception exception)
            {
                string actionName = applyTransfer
                    ? "переносе Selection Sets"
                    : "проверке соответствий Selection Sets";
                string recoveryNote = applyTransfer
                    ? "\n\nЕсли вы уже подтвердили перенос, проверьте текущее окно Sets. NWF автоматически не сохранялся."
                    : "";

                MessageBox.Show(
                    "Ошибка при " + actionName + ":\n\n" + exception + recoveryNote,
                    "Navis Coordinator",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private static bool IsTransferableItemStatus(string status)
        {
            return string.Equals(status, "MATCHED", StringComparison.Ordinal) ||
                   string.Equals(status, "MATCHED_ONE_TO_MANY_CATEGORY", StringComparison.Ordinal) ||
                   string.Equals(status, "MATCHED_ONE_TO_MANY_IDENTITY", StringComparison.Ordinal) ||
                   string.Equals(status, "MATCHED_ONE_TO_MANY_STRUCTURAL", StringComparison.Ordinal) ||
                   string.Equals(status, "MATCHED_ONE_TO_MANY_NATIVE_POLICY", StringComparison.Ordinal);
        }

        private static bool ValidateTransferPreconditions(
            Document document,
            List<ExpectedSet> expectedSets,
            Dictionary<ExpectedSet, Dictionary<string, ModelItem>> resolvedTargetItemsBySet,
            List<object> rootMappingReports,
            List<object> nativeRuleExpansionReports,
            int expectedItemCount,
            int resolvedItemCount,
            int missingItems,
            int ambiguousItems,
            int searchLimitItems,
            int identityWarningCount,
            bool searchLimitReached,
            bool nativeRebuildMode,
            out string details)
        {
            List<string> problems = new List<string>();

            if (document == null || document.IsClear)
                problems.Add("Нет открытого целевого документа.");

            if (expectedSets == null || expectedSets.Count == 0)
                problems.Add("В исходном JSON нет наборов для переноса.");

            if (expectedItemCount == 0 || resolvedItemCount != expectedItemCount)
                problems.Add("Разрешено узлов: " + resolvedItemCount + " из " + expectedItemCount + ".");

            if (missingItems != 0)
                problems.Add("Не найдено исходных узлов: " + missingItems + ".");

            if (ambiguousItems != 0)
                problems.Add("Остались неоднозначные соответствия: " + ambiguousItems + ".");

            if (searchLimitItems != 0 || searchLimitReached)
                problems.Add("Поиск завершился по лимиту; перенос в таком состоянии запрещён.");

            if (identityWarningCount != 0)
                problems.Add("Есть предупреждения по идентификаторам: " + identityWarningCount + ".");

            HashSet<string> sourceSetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (expectedSets != null)
            {
                foreach (ExpectedSet expectedSet in expectedSets)
                {
                    if (expectedSet == null || string.IsNullOrWhiteSpace(expectedSet.Name))
                    {
                        problems.Add("Обнаружен Selection Set без имени.");
                        continue;
                    }

                    if (!sourceSetNames.Add(expectedSet.Name))
                        problems.Add("В исходном JSON повторяется имя набора '" + expectedSet.Name + "'.");

                    Dictionary<string, ModelItem> resolvedItems;
                    if (resolvedTargetItemsBySet == null ||
                        !resolvedTargetItemsBySet.TryGetValue(expectedSet, out resolvedItems) ||
                        resolvedItems == null || resolvedItems.Count == 0)
                    {
                        problems.Add("Для набора '" + expectedSet.Name + "' не найдено целевых узлов.");
                    }
                }
            }

            if (rootMappingReports == null || rootMappingReports.Count == 0)
            {
                problems.Add("Отчёт сопоставления корней пуст.");
            }
            else
            {
                foreach (object rawMapping in rootMappingReports)
                {
                    Dictionary<string, object> mapping = AsDictionary(rawMapping);
                    if (mapping == null)
                    {
                        problems.Add("Не удалось прочитать запись сопоставления корня.");
                        continue;
                    }

                    string mappingStatus = GetDictionaryString(mapping, "mappingStatus");
                    int targetRootCount = ToInt32(GetDictionaryValue(mapping, "targetRootCount"));
                    string sourceRootName = GetDictionaryString(mapping, "sourceRootName");

                    if (!string.Equals(mappingStatus, "FOUND", StringComparison.Ordinal) || targetRootCount != 1)
                    {
                        problems.Add("Корень '" + sourceRootName + "': статус=" + mappingStatus +
                                     ", найдено целевых корней=" + targetRootCount + ". Требуется ровно один.");
                    }
                }
            }

            if (nativeRebuildMode && nativeRuleExpansionReports != null)
            {
                foreach (object rawRuleReport in nativeRuleExpansionReports)
                {
                    Dictionary<string, object> ruleReport = AsDictionary(rawRuleReport);
                    if (ruleReport == null) continue;
                    string ruleStatus = GetDictionaryString(ruleReport, "status");
                    if (string.Equals(ruleStatus, "REQUIRED_CATEGORY_NOT_FOUND", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(ruleStatus, "SEARCH_TRUNCATED", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(ruleStatus, "SEARCH_LIMIT_REACHED", StringComparison.OrdinalIgnoreCase) ||
                        ruleStatus.StartsWith("BLOCKED_", StringComparison.OrdinalIgnoreCase))
                    {
                        problems.Add("Native rule expansion для раздела '" +
                            GetDictionaryString(ruleReport, "sectionCode") + "' не прошёл проверку: " + ruleStatus + ".");
                    }
                }
            }

            if (document != null && !document.IsClear)
            {
                HashSet<string> existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                CollectSavedItemNames(document.SelectionSets.RootItem.Children, existingNames);
                if (expectedSets != null)
                {
                    foreach (ExpectedSet expectedSet in expectedSets)
                    {
                        if (expectedSet != null && !string.IsNullOrWhiteSpace(expectedSet.Name) &&
                            existingNames.Contains(expectedSet.Name))
                        {
                            if (!nativeRebuildMode)
                            {
                                problems.Add("В целевом документе уже существует набор или папка с именем '" +
                                             expectedSet.Name + "'. Ничего не будет перезаписано.");
                            }
                            else
                            {
                                SavedItem topLevel = FindTopLevelSavedItemByName(document, expectedSet.Name);
                                int nameCount = CountSavedItemsNamed(
                                    document.SelectionSets.RootItem.Children,
                                    expectedSet.Name);
                                if (!(topLevel is SelectionSet) || nameCount != 1)
                                {
                                    problems.Add("В нативном режиме имя '" + expectedSet.Name +
                                        "' можно переиспользовать только при наличии ровно одного одноимённого Selection Set верхнего уровня. " +
                                        "Обнаружено совпадений: " + nameCount + ". Исправьте структуру Sets или используйте чистую рабочую копию.");
                                }
                            }
                        }
                    }
                }
            }

            details = problems.Count == 0 ? "" : string.Join(Environment.NewLine, problems.ToArray());
            return problems.Count == 0;
        }

        private static int ToInt32(object value)
        {
            if (value == null)
                return 0;

            try
            {
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return 0;
            }
        }

        private static void CollectSavedItemNames(SavedItemCollection collection, HashSet<string> names)
        {
            if (collection == null || names == null)
                return;

            foreach (SavedItem savedItem in collection)
            {
                if (savedItem == null)
                    continue;

                if (!string.IsNullOrWhiteSpace(savedItem.DisplayName))
                    names.Add(savedItem.DisplayName);

                FolderItem folder = savedItem as FolderItem;
                if (folder != null)
                    CollectSavedItemNames(folder.Children, names);
            }
        }

        private static SavedItem FindTopLevelSavedItemByName(Document document, string displayName)
        {
            if (document == null || document.IsClear || string.IsNullOrWhiteSpace(displayName))
                return null;

            foreach (SavedItem savedItem in document.SelectionSets.RootItem.Children)
            {
                if (savedItem != null &&
                    string.Equals(savedItem.DisplayName ?? "", displayName, StringComparison.OrdinalIgnoreCase))
                    return savedItem;
            }

            return null;
        }

        private static int CountResolvedTargetItems(
            List<ExpectedSet> expectedSets,
            Dictionary<ExpectedSet, Dictionary<string, ModelItem>> resolvedTargetItemsBySet)
        {
            int count = 0;
            if (expectedSets == null || resolvedTargetItemsBySet == null)
                return count;

            foreach (ExpectedSet expectedSet in expectedSets)
            {
                Dictionary<string, ModelItem> targetItems;
                if (resolvedTargetItemsBySet.TryGetValue(expectedSet, out targetItems) && targetItems != null)
                    count += targetItems.Count;
            }

            return count;
        }

        private static void CreateSelectionSets(
            Document document,
            List<ExpectedSet> expectedSets,
            Dictionary<ExpectedSet, Dictionary<string, ModelItem>> resolvedTargetItemsBySet,
            out int createdSelectionSetCount,
            out int createdTargetModelItemCount)
        {
            createdSelectionSetCount = 0;
            createdTargetModelItemCount = 0;
            List<string> addedSetNames = new List<string>();
            Dictionary<string, int> addedSetItemCounts =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (ExpectedSet expectedSet in expectedSets)
                {
                    Dictionary<string, ModelItem> targetItemsByPath;
                    if (!resolvedTargetItemsBySet.TryGetValue(expectedSet, out targetItemsByPath) ||
                        targetItemsByPath == null || targetItemsByPath.Count == 0)
                    {
                        throw new InvalidOperationException(
                            "Для Selection Set '" + expectedSet.Name + "' не подготовлены целевые узлы.");
                    }

                    ModelItemCollection targetItems = new ModelItemCollection();
                    foreach (KeyValuePair<string, ModelItem> pair in targetItemsByPath)
                    {
                        if (pair.Value != null)
                            targetItems.Add(pair.Value);
                    }

                    if (targetItems.Count == 0)
                    {
                        throw new InvalidOperationException(
                            "Selection Set '" + expectedSet.Name + "' после удаления пустых ссылок не содержит узлов.");
                    }

                    SelectionSet newSet = new SelectionSet(targetItems);
                    newSet.DisplayName = expectedSet.Name;
                    document.SelectionSets.AddCopy(newSet);
                    addedSetNames.Add(expectedSet.Name);
                    addedSetItemCounts[expectedSet.Name] = targetItems.Count;

                    SavedItem addedItem = FindTopLevelSavedItemByName(document, expectedSet.Name);
                    if (addedItem == null)
                    {
                        throw new InvalidOperationException(
                            "Navisworks добавил набор '" + expectedSet.Name + "', но плагин не смог проверить его наличие в корне Sets.");
                    }

                    createdSelectionSetCount++;
                    createdTargetModelItemCount += targetItems.Count;
                }
            }
            catch (Exception originalException)
            {
                List<string> rollbackProblems = new List<string>();
                for (int index = addedSetNames.Count - 1; index >= 0; index--)
                {
                    string setName = addedSetNames[index];
                    try
                    {
                        SavedItem addedItem = FindTopLevelSavedItemByName(document, setName);
                        // A null item means it is already absent, so rollback has nothing left to do.
                        if (addedItem != null && !document.SelectionSets.Remove(addedItem))
                            rollbackProblems.Add("Не удалось удалить набор '" + setName + "'.");
                    }
                    catch (Exception rollbackException)
                    {
                        rollbackProblems.Add("'" + setName + "': " + rollbackException.Message);
                    }
                }

                if (rollbackProblems.Count > 0)
                {
                    // Report the sets that still appear in the document after rollback attempts.
                    createdSelectionSetCount = 0;
                    createdTargetModelItemCount = 0;
                    foreach (string setName in addedSetNames)
                    {
                        if (FindTopLevelSavedItemByName(document, setName) == null)
                            continue;

                        createdSelectionSetCount++;
                        int itemCount;
                        if (addedSetItemCounts.TryGetValue(setName, out itemCount))
                            createdTargetModelItemCount += itemCount;
                    }

                    throw new InvalidOperationException(
                        "Создание Selection Sets завершилось ошибкой. При попытке отката возникли проблемы:\n" +
                        string.Join(Environment.NewLine, rollbackProblems.ToArray()) +
                        "\nИсходная ошибка: " + originalException.Message,
                        originalException);
                }

                createdSelectionSetCount = 0;
                createdTargetModelItemCount = 0;
                throw new InvalidOperationException(
                    "Создание Selection Sets завершилось ошибкой. Наборы, добавленные этой операцией, удалены.\n" +
                    originalException.Message,
                    originalException);
            }
        }

        // Diagnostic export only: this method reads the model tree and writes CSV.
        // It deliberately does not touch PropertyCategories, DataProperty, or SelectionSets.
        private const int MaxDumpNodesPerModel = 500000;

        private static void RunTreeDump(Document document)
        {
            if (document == null || document.Models == null)
            {
                MessageBox.Show(
                    "Нет открытого документа или загруженных моделей.",
                    "Navis Coordinator — дамп дерева",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Выберите папку для диагностических CSV-файлов дерева Navisworks.";
                dialog.SelectedPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                dialog.ShowNewFolderButton = true;

                if (dialog.ShowDialog() != DialogResult.OK)
                    return;

                int modelIndex = 0;
                int totalRows = 0;
                int filesWritten = 0;
                List<string> outputFiles = new List<string>();

                foreach (Model model in document.Models)
                {
                    if (model == null || model.RootItem == null)
                        continue;

                    modelIndex++;
                    string rootName = SafeDisplayName(model.RootItem);
                    string safeRootName = SafeFileNamePart(rootName);
                    if (string.IsNullOrWhiteSpace(safeRootName))
                        safeRootName = "Model";

                    string fileName = "NavisCoordinator_TreeDump_" +
                                      DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) +
                                      "_" + modelIndex.ToString("D2", CultureInfo.InvariantCulture) +
                                      "_" + safeRootName + ".csv";
                    string filePath = Path.Combine(dialog.SelectedPath, fileName);
                    bool truncated;
                    int rows = DumpOneModelTree(model.RootItem, filePath, out truncated);

                    totalRows += rows;
                    filesWritten++;
                    outputFiles.Add(fileName + " — строк: " + rows +
                                    (truncated ? " (достигнут лимит узлов)" : ""));
                }

                if (filesWritten == 0)
                {
                    MessageBox.Show(
                        "В документе не обнаружены корневые узлы загруженных моделей.",
                        "Navis Coordinator — дамп дерева",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return;
                }

                StringBuilder message = new StringBuilder();
                message.AppendLine("Диагностический дамп завершён. Модель не изменялась.");
                message.AppendLine();
                message.AppendLine("Файлов: " + filesWritten);
                message.AppendLine("Строк дерева: " + totalRows);
                message.AppendLine("Папка: " + dialog.SelectedPath);
                message.AppendLine();
                foreach (string outputFile in outputFiles)
                    message.AppendLine(outputFile);

                MessageBox.Show(
                    message.ToString(),
                    "Navis Coordinator — дамп дерева",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
        }

        private static int DumpOneModelTree(ModelItem root, string filePath, out bool truncated)
        {
            truncated = false;
            if (root == null)
                return 0;

            int rowsWritten = 0;
            int nextId = 1;
            Queue<TreeDumpQueueItem> queue = new Queue<TreeDumpQueueItem>();

            string rootName = SafeDisplayName(root);
            TreeDumpQueueItem rootEntry = new TreeDumpQueueItem();
            rootEntry.Item = root;
            rootEntry.Id = 0;
            rootEntry.ParentId = -1;
            rootEntry.Depth = 0;
            queue.Enqueue(rootEntry);

            using (StreamWriter writer = new StreamWriter(filePath, false, new UTF8Encoding(true)))
            {
                writer.WriteLine(
                    "id;parentId;depth;childCount;hasGeometry;className;classDisplayName;displayName;childrenStatus");

                while (queue.Count > 0)
                {
                    TreeDumpQueueItem entry = queue.Dequeue();
                    List<ModelItem> children = new List<ModelItem>();
                    string childrenStatus = "OK";

                    try
                    {
                        foreach (ModelItem child in entry.Item.Children)
                        {
                            if (child == null)
                                continue;

                            if (nextId >= MaxDumpNodesPerModel)
                            {
                                truncated = true;
                                childrenStatus = "NODE_LIMIT_REACHED";
                                break;
                            }

                            string childName = SafeDisplayName(child);
                            TreeDumpQueueItem childEntry = new TreeDumpQueueItem();
                            childEntry.Item = child;
                            childEntry.Id = nextId++;
                            childEntry.ParentId = entry.Id;
                            childEntry.Depth = entry.Depth + 1;

                            children.Add(child);
                            queue.Enqueue(childEntry);
                        }
                    }
                    catch (Exception exception)
                    {
                        childrenStatus = "CHILDREN_READ_ERROR:" + exception.GetType().Name;
                    }

                    string displayName = SafeDisplayName(entry.Item);
                    string className = SafeModelItemClassName(entry.Item);
                    string classDisplayName = SafeModelItemClassDisplayName(entry.Item);
                    string hasGeometry = SafeModelItemHasGeometry(entry.Item);

                    writer.WriteLine(
                        entry.Id.ToString(CultureInfo.InvariantCulture) + ";" +
                        entry.ParentId.ToString(CultureInfo.InvariantCulture) + ";" +
                        entry.Depth.ToString(CultureInfo.InvariantCulture) + ";" +
                        children.Count.ToString(CultureInfo.InvariantCulture) + ";" +
                        CsvField(hasGeometry) + ";" +
                        CsvField(className) + ";" +
                        CsvField(classDisplayName) + ";" +
                        CsvField(displayName) + ";" +
                        CsvField(childrenStatus));

                    rowsWritten++;
                }

                if (truncated)
                {
                    writer.WriteLine(
                        "#;#;#;#;#;#;#;#;" +
                        CsvField("TRUNCATED: достигнут MaxDumpNodesPerModel=" +
                                 MaxDumpNodesPerModel.ToString(CultureInfo.InvariantCulture)));
                }
            }

            return rowsWritten;
        }

        private static string SafeModelItemClassName(ModelItem item)
        {
            try { return item == null ? "" : item.ClassName ?? ""; }
            catch (Exception exception) { return "<ERR:" + exception.GetType().Name + ">"; }
        }

        private static string SafeModelItemClassDisplayName(ModelItem item)
        {
            try { return item == null ? "" : item.ClassDisplayName ?? ""; }
            catch (Exception exception) { return "<ERR:" + exception.GetType().Name + ">"; }
        }

        private static string SafeModelItemHasGeometry(ModelItem item)
        {
            try { return item == null ? "" : item.HasGeometry.ToString(); }
            catch (Exception exception) { return "<ERR:" + exception.GetType().Name + ">"; }
        }

        private static string CsvField(string value)
        {
            if (value == null)
                value = "";
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private static string SafeFileNamePart(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";

            StringBuilder builder = new StringBuilder(value.Length);
            foreach (char character in value)
            {
                if (Array.IndexOf(Path.GetInvalidFileNameChars(), character) >= 0 ||
                    char.IsControl(character))
                {
                    builder.Append('_');
                }
                else
                {
                    builder.Append(character);
                }
            }

            string result = builder.ToString().Trim().Trim('.');
            if (result.Length > 80)
                result = result.Substring(0, 80);
            return result;
        }

        private sealed class TreeDumpQueueItem
        {
            public ModelItem Item;
            public int Id;
            public int ParentId;
            public int Depth;
        }

        private static List<string> GetRequiredRootNames(List<ExpectedSet> expectedSets)
        {
            List<string> roots = new List<string>();
            HashSet<string> seenNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (ExpectedSet expectedSet in expectedSets)
            {
                foreach (ExpectedItem expectedItem in expectedSet.Items)
                {
                    List<string> pathSegments = GetSourcePathSegments(expectedItem.HierarchyNodes);
                    if (pathSegments.Count == 0)
                        continue;

                    string rootName = pathSegments[0];
                    if (seenNames.Add(rootName))
                        roots.Add(rootName);
                }
            }

            return roots;
        }

        private static List<ModelItem> FindAllLinkedModelRoots(
            Document document,
            ref int nodesInspected)
        {
            List<ModelItem> results = new List<ModelItem>();
            if (document == null || document.Models == null)
                return results;

            foreach (Model model in document.Models)
            {
                if (model == null || model.RootItem == null)
                    continue;

                Queue<KeyValuePair<ModelItem, int>> queue =
                    new Queue<KeyValuePair<ModelItem, int>>();
                queue.Enqueue(new KeyValuePair<ModelItem, int>(model.RootItem, 0));

                while (queue.Count > 0)
                {
                    KeyValuePair<ModelItem, int> entry = queue.Dequeue();
                    ModelItem item = entry.Key;
                    int depth = entry.Value;
                    if (item == null)
                        continue;

                    CountInspectedNode(ref nodesInspected);
                    string className = SafeModelItemClassName(item);
                    if (string.Equals(className, "LcRevitLink", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrWhiteSpace(ExtractModelFileName(SafeDisplayName(item))))
                            results.Add(item);

                        // A link root represents the complete linked model. Do not scan
                        // its category/level descendants for additional root candidates.
                        continue;
                    }

                    if (depth >= MaxRootSearchDepth)
                        continue;

                    foreach (ModelItem child in item.Children)
                    {
                        if (child != null)
                        {
                            queue.Enqueue(new KeyValuePair<ModelItem, int>(child, depth + 1));
                        }
                    }
                }
            }

            return results;
        }

        private static bool TryGetProjectAndStageToken(
            string fileOrDisplayName,
            out string projectPrefix,
            out string stageToken)
        {
            projectPrefix = "";
            stageToken = "";
            if (string.IsNullOrWhiteSpace(fileOrDisplayName))
                return false;

            string fileName = Path.GetFileName(fileOrDisplayName.Trim());
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = fileOrDisplayName.Trim();

            int projectMarkerIndex = fileName.IndexOf("_РД_", StringComparison.OrdinalIgnoreCase);
            if (projectMarkerIndex <= 0)
                return false;

            projectPrefix = fileName.Substring(0, projectMarkerIndex);
            int afterProjectMarker = projectMarkerIndex + "_РД_".Length;
            int stageLetterIndex;

            // The NWF itself is named like "..._РД_П1_СБ.nwf", where the stage
            // immediately follows _РД_. A linked RVT is named like
            // "..._РД_АСКУЭ_П1_R23.rvt", where the stage follows the section code.
            if (afterProjectMarker < fileName.Length &&
                string.Equals(fileName.Substring(afterProjectMarker, 1), "П", StringComparison.OrdinalIgnoreCase))
            {
                stageLetterIndex = afterProjectMarker;
            }
            else
            {
                int stageMarkerIndex = fileName.IndexOf(
                    "_П",
                    afterProjectMarker,
                    StringComparison.OrdinalIgnoreCase);
                if (stageMarkerIndex < 0)
                    return false;

                stageLetterIndex = stageMarkerIndex + 1;
            }

            int digitsStart = stageLetterIndex + 1;
            int digitsEnd = digitsStart;
            while (digitsEnd < fileName.Length && char.IsDigit(fileName[digitsEnd]))
                digitsEnd++;

            if (digitsEnd == digitsStart)
                return false;

            stageToken = "П" + fileName.Substring(digitsStart, digitsEnd - digitsStart);
            return true;
        }

        private static string ExtractModelSectionCode(string modelFileName)
        {
            string fileName = ExtractModelFileName(modelFileName);
            if (string.IsNullOrWhiteSpace(fileName))
                return "";

            int extensionIndex = fileName.IndexOf(".rvt", StringComparison.OrdinalIgnoreCase);
            string baseName = extensionIndex >= 0
                ? fileName.Substring(0, extensionIndex)
                : fileName;

            int projectMarkerIndex = baseName.IndexOf("_РД_", StringComparison.OrdinalIgnoreCase);
            if (projectMarkerIndex < 0)
                return "";

            int codeStart = projectMarkerIndex + "_РД_".Length;
            int stageMarkerIndex = baseName.IndexOf(
                "_П",
                codeStart,
                StringComparison.OrdinalIgnoreCase);
            if (stageMarkerIndex <= codeStart)
                return "";

            return baseName.Substring(codeStart, stageMarkerIndex - codeStart).Trim('_', ' ');
        }

        private static string TransliterateSectionCodeToSetName(string sectionCode)
        {
            if (string.IsNullOrWhiteSpace(sectionCode))
                return "";

            StringBuilder result = new StringBuilder();
            foreach (char rawCharacter in sectionCode.Trim())
            {
                char character = char.ToUpperInvariant(rawCharacter);
                switch (character)
                {
                    case 'А': result.Append("A"); break;
                    case 'Б': result.Append("B"); break;
                    case 'В': result.Append("V"); break;
                    case 'Г': result.Append("G"); break;
                    case 'Д': result.Append("D"); break;
                    case 'Е': result.Append("E"); break;
                    case 'Ё': result.Append("E"); break;
                    case 'Ж': result.Append("ZH"); break;
                    case 'З': result.Append("Z"); break;
                    case 'И': result.Append("I"); break;
                    case 'Й': result.Append("Y"); break;
                    case 'К': result.Append("K"); break;
                    case 'Л': result.Append("L"); break;
                    case 'М': result.Append("M"); break;
                    case 'Н': result.Append("N"); break;
                    case 'О': result.Append("O"); break;
                    case 'П': result.Append("P"); break;
                    case 'Р': result.Append("R"); break;
                    case 'С': result.Append("S"); break;
                    case 'Т': result.Append("T"); break;
                    case 'У': result.Append("U"); break;
                    case 'Ф': result.Append("F"); break;
                    case 'Х': result.Append("KH"); break;
                    case 'Ц': result.Append("TS"); break;
                    case 'Ч': result.Append("CH"); break;
                    case 'Ш': result.Append("SH"); break;
                    case 'Щ': result.Append("SHCH"); break;
                    case 'Ъ': break;
                    case 'Ы': result.Append("Y"); break;
                    case 'Ь': break;
                    case 'Э': result.Append("E"); break;
                    case 'Ю': result.Append("YU"); break;
                    case 'Я': result.Append("YA"); break;
                    default:
                        if (char.IsLetterOrDigit(character))
                            result.Append(character);
                        break;
                }
            }

            return result.ToString().ToUpperInvariant();
        }

        private static int CountNativePolicySetsByMode(List<ExpectedSet> sets, string mode)
        {
            int count = 0;
            if (sets == null) return count;
            foreach (ExpectedSet set in sets)
            {
                if (set != null && set.IsNativePolicySet &&
                    string.Equals(set.NativeMode, mode, StringComparison.OrdinalIgnoreCase))
                    count++;
            }
            return count;
        }

        private static int CountNativePolicySetsBySections(List<ExpectedSet> sets, string[] sectionCodes)
        {
            int count = 0;
            if (sets == null || sectionCodes == null) return count;
            foreach (ExpectedSet set in sets)
            {
                if (set == null || !set.IsNativePolicySet) continue;
                foreach (string sectionCode in sectionCodes)
                {
                    if (string.Equals(set.NativeSectionCode, sectionCode, StringComparison.OrdinalIgnoreCase))
                    {
                        count++;
                        break;
                    }
                }
            }
            return count;
        }

        private static Dictionary<string, object> BuildCoverageAnalysis(
            List<ExpectedSet> expectedSets,
            Dictionary<string, List<TargetNodeInfo>> indexedRootsByName)
        {
            const int maxInspectedNodesPerRoot = 150000;
            Dictionary<string, object> coverage = new Dictionary<string, object>();
            coverage["formatVersion"] = 1;
            coverage["analysisType"] = "target-category-inventory-vs-native-section-policy";
            coverage["autoIncludeUnlistedCategories"] = false;
            coverage["unlistedCategoryHandling"] = "report-only; no unlisted category is added to a Selection Set automatically";
            coverage["maxNodesInspectedPerRoot"] = maxInspectedNodesPerRoot;

            Dictionary<string, ExpectedSet> bySection = new Dictionary<string, ExpectedSet>(StringComparer.OrdinalIgnoreCase);
            if (expectedSets != null)
            {
                foreach (ExpectedSet set in expectedSets)
                {
                    if (set != null && !string.IsNullOrWhiteSpace(set.NativeSectionCode))
                        bySection[set.NativeSectionCode] = set;
                }
            }

            List<object> sectionReports = new List<object>();
            int totalObservedCategoryNames = 0;
            int totalUnclassifiedCategoryNames = 0;
            int incompleteRootScans = 0;

            foreach (KeyValuePair<string, ExpectedSet> sectionPair in bySection)
            {
                ExpectedSet set = sectionPair.Value;
                Dictionary<string, object> sectionReport = new Dictionary<string, object>();
                sectionReport["sectionCode"] = set.NativeSectionCode;
                sectionReport["selectionSetName"] = set.Name;
                sectionReport["mode"] = set.NativeMode;
                sectionReport["sourceModelFileName"] = set.NativeSourceModelFileName;
                sectionReport["expectedTargetModelFileName"] = set.NativeExpectedTargetModelFileName;

                List<string> selectedWholeCategories = new List<string>();
                HashSet<string> selectedDescendantCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (ExpectedItem expectedItem in set.Items)
                {
                    List<string> pathSegments = GetSourcePathSegments(expectedItem.HierarchyNodes);
                    if (pathSegments.Count < 2) continue;
                    string categoryName = pathSegments[1];
                    string terminalType = GetExpectedTerminalNodeType(expectedItem);
                    if (pathSegments.Count == 2 && IsCategoryType(terminalType))
                        selectedWholeCategories.Add(categoryName);
                    else
                        selectedDescendantCategories.Add(categoryName);
                }

                TargetNodeInfo rootInfo = null;
                List<TargetNodeInfo> possibleRoots;
                if (indexedRootsByName != null &&
                    indexedRootsByName.TryGetValue(set.NativeSourceModelFileName, out possibleRoots) &&
                    possibleRoots != null && possibleRoots.Count == 1)
                    rootInfo = possibleRoots[0];

                if (rootInfo == null || rootInfo.Item == null)
                {
                    sectionReport["inventoryStatus"] = "TARGET_ROOT_NOT_INDEXED_UNIQUELY";
                    sectionReport["observedCategoryCount"] = 0;
                    sectionReport["categories"] = new List<object>();
                    sectionReports.Add(sectionReport);
                    incompleteRootScans++;
                    continue;
                }

                Dictionary<string, int> observedCategoryCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                Queue<ModelItem> queue = new Queue<ModelItem>();
                HashSet<ModelItem> visited = new HashSet<ModelItem>();
                queue.Enqueue(rootInfo.Item);
                int inspected = 0;
                bool truncated = false;

                while (queue.Count > 0)
                {
                    ModelItem current = queue.Dequeue();
                    if (current == null || !visited.Add(current)) continue;
                    inspected++;
                    if (inspected > maxInspectedNodesPerRoot)
                    {
                        truncated = true;
                        break;
                    }

                    string elementType = GetModelItemPropertyValue(current, "Элемент", "Тип");
                    bool isCategory = IsCategoryType(elementType);
                    if (isCategory)
                    {
                        string categoryName = SafeDisplayName(current);
                        if (!string.IsNullOrWhiteSpace(categoryName))
                        {
                            int occurrences;
                            observedCategoryCounts.TryGetValue(categoryName, out occurrences);
                            observedCategoryCounts[categoryName] = occurrences + 1;
                        }
                    }

                    bool shouldExpand;
                    if (inspected == 1)
                        shouldExpand = true;
                    else
                    {
                        string className = SafeModelItemClassName(current);
                        shouldExpand = string.Equals(className, "LcRevitLayer", StringComparison.OrdinalIgnoreCase) || isCategory;
                    }

                    if (!shouldExpand) continue;
                    try
                    {
                        foreach (ModelItem child in current.Children)
                            if (child != null) queue.Enqueue(child);
                    }
                    catch
                    {
                        // Preserve a useful partial coverage report; preflight matching
                        // separately decides whether the selected paths are resolvable.
                    }
                }

                List<string> observedNames = new List<string>(observedCategoryCounts.Keys);
                observedNames.Sort(StringComparer.OrdinalIgnoreCase);
                List<object> categoryReports = new List<object>();
                int sectionUnclassified = 0;
                foreach (string categoryName in observedNames)
                {
                    bool wholeLink = string.Equals(set.NativeMode, "wholeLink", StringComparison.OrdinalIgnoreCase);
                    bool selectedWhole = selectedWholeCategories.Exists(x => string.Equals(x, categoryName, StringComparison.OrdinalIgnoreCase));
                    bool selectedDescendant = selectedDescendantCategories.Contains(categoryName);
                    string classification;
                    string status;
                    if (wholeLink)
                    {
                        classification = "coveredByWholeLink";
                        status = "includedByPolicy";
                    }
                    else if (selectedWhole)
                    {
                        classification = "selectedCategoryWhole";
                        status = "includedByPolicy";
                    }
                    else if (selectedDescendant)
                    {
                        // The category itself is not selected as one broad node, but configured
                        // descendants (for example, allowed pipe types under VK / Трубы) are.
                        classification = "partiallyCoveredByDescendants";
                        status = "includedByPolicy_partially";
                    }
                    else if (IsNativeRuleExcludedCategory(set, categoryName))
                    {
                        // Exclusions belong to one section only. For example, pipe insulation is
                        // excluded from VK while the whole Трубы category may still be selected
                        // by another section's policy.
                        classification = "explicitlyExcludedBySectionRule";
                        status = "explicitlyNotSelected_byRule";
                    }
                    else
                    {
                        string hint = GetNativeCategoryClassificationHint(set.NativeSectionCode, categoryName);
                        if (!string.IsNullOrWhiteSpace(hint))
                        {
                            classification = hint;
                            status = "explicitlyNotSelected_byConvention";
                        }
                        else
                        {
                            classification = "newOrUnclassified";
                            status = "reviewNotAutoIncluded";
                            sectionUnclassified++;
                        }
                    }

                    Dictionary<string, object> categoryReport = new Dictionary<string, object>();
                    categoryReport["categoryName"] = categoryName;
                    categoryReport["occurrencesAcrossLevels"] = observedCategoryCounts[categoryName];
                    categoryReport["coverageStatus"] = status;
                    categoryReport["classification"] = classification;
                    categoryReports.Add(categoryReport);
                }

                sectionReport["inventoryStatus"] = truncated ? "TRUNCATED_NODE_LIMIT" : "COMPLETE_WITHIN_POLICY_TREE_RULES";
                sectionReport["nodesInspected"] = Math.Min(inspected, maxInspectedNodesPerRoot);
                sectionReport["truncated"] = truncated;
                sectionReport["observedCategoryCount"] = observedCategoryCounts.Count;
                sectionReport["unclassifiedCategoryCount"] = sectionUnclassified;
                sectionReport["categories"] = categoryReports;
                sectionReport["wholeLinkCoversAllObservedCategories"] = string.Equals(set.NativeMode, "wholeLink", StringComparison.OrdinalIgnoreCase);
                sectionReports.Add(sectionReport);
                totalObservedCategoryNames += observedCategoryCounts.Count;
                totalUnclassifiedCategoryNames += sectionUnclassified;
                if (truncated) incompleteRootScans++;
            }

            coverage["sections"] = sectionReports;
            coverage["sectionCount"] = sectionReports.Count;
            coverage["totalObservedCategoryNameCount"] = totalObservedCategoryNames;
            coverage["totalUnclassifiedCategoryNameCount"] = totalUnclassifiedCategoryNames;
            coverage["incompleteRootScanCount"] = incompleteRootScans;
            coverage["isComplete"] = incompleteRootScans == 0;
            return coverage;
        }

        private static bool IsNativeRuleExcludedCategory(ExpectedSet set, string categoryName)
        {
            if (set == null || set.NativeRuleConfig == null || string.IsNullOrWhiteSpace(categoryName))
                return false;

            List<string> excludedNames = ReadNativeRuleStringList(
                set.NativeRuleConfig,
                "categoryExcludeList");
            return ContainsIgnoreCase(excludedNames, categoryName);
        }

        private static string GetNativeCategoryClassificationHint(string sectionCode, string categoryName)
        {
            if (string.Equals(categoryName, "Осевая линия", StringComparison.OrdinalIgnoreCase))
                return "known-system-category-not-in-selection-policy";

            // sectionCode now describes the ACTUAL target linked model, not the legacy source set.
            // Actual OV1 is the pipe model; actual OV2 is the duct model.
            if ((string.Equals(sectionCode, "ВК", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(sectionCode, "ОВ1", StringComparison.OrdinalIgnoreCase)) &&
                string.Equals(categoryName, "Материалы изоляции труб", StringComparison.OrdinalIgnoreCase))
                return "known-not-selected-in-P1-policy";

            if (string.Equals(sectionCode, "ОВ2", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(categoryName, "Материалы изоляции воздуховодов", StringComparison.OrdinalIgnoreCase))
                return "known-not-selected-in-P1-policy";

            if (string.Equals(sectionCode, "СС", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(categoryName, "Гибкие трубы", StringComparison.OrdinalIgnoreCase))
                return "explicitly-excluded-for-CC";

            return "";
        }

        private static int CountSavedItemsNamed(SavedItemCollection collection, string displayName)
        {
            if (collection == null || string.IsNullOrWhiteSpace(displayName)) return 0;
            int count = 0;
            foreach (SavedItem item in collection)
            {
                if (item == null) continue;
                if (string.Equals(item.DisplayName ?? "", displayName, StringComparison.OrdinalIgnoreCase))
                    count++;
                FolderItem folder = item as FolderItem;
                if (folder != null)
                    count += CountSavedItemsNamed(folder.Children, displayName);
            }
            return count;
        }

        private static int CountReusableTargetSets(Document document, List<ExpectedSet> expectedSets)
        {
            if (document == null || document.IsClear || expectedSets == null) return 0;
            int count = 0;
            foreach (ExpectedSet expectedSet in expectedSets)
            {
                if (expectedSet == null || string.IsNullOrWhiteSpace(expectedSet.Name)) continue;
                int matches = CountSavedItemsNamed(document.SelectionSets.RootItem.Children, expectedSet.Name);
                if (matches == 1 && FindTopLevelSavedItemByName(document, expectedSet.Name) is SelectionSet)
                    count++;
            }
            return count;
        }

        private sealed class NativeSetChange
        {
            public string Name = "";
            public bool WasReplacement;
            public SavedItem Backup;
            public GroupItem Parent;
            public int Index;
            public int TargetItemCount;
        }

        private static void CreateOrReplaceNativeSelectionSets(
            Document document,
            List<ExpectedSet> expectedSets,
            Dictionary<ExpectedSet, Dictionary<string, ModelItem>> resolvedTargetItemsBySet,
            out int createdSelectionSetCount,
            out int replacedSelectionSetCount,
            out int createdTargetModelItemCount)
        {
            createdSelectionSetCount = 0;
            replacedSelectionSetCount = 0;
            createdTargetModelItemCount = 0;
            List<NativeSetChange> changes = new List<NativeSetChange>();

            try
            {
                foreach (ExpectedSet expectedSet in expectedSets)
                {
                    Dictionary<string, ModelItem> targetItemsByPath;
                    if (!resolvedTargetItemsBySet.TryGetValue(expectedSet, out targetItemsByPath) ||
                        targetItemsByPath == null || targetItemsByPath.Count == 0)
                    {
                        throw new InvalidOperationException("Для нативного Selection Set '" + expectedSet.Name + "' не подготовлены целевые узлы.");
                    }

                    ModelItemCollection targetItems = new ModelItemCollection();
                    foreach (KeyValuePair<string, ModelItem> pair in targetItemsByPath)
                        if (pair.Value != null) targetItems.Add(pair.Value);
                    if (targetItems.Count == 0)
                        throw new InvalidOperationException("Нативный Selection Set '" + expectedSet.Name + "' пуст после разрешения ссылок.");

                    SelectionSet newSet = new SelectionSet(targetItems);
                    newSet.DisplayName = expectedSet.Name;

                    int namedCount = CountSavedItemsNamed(document.SelectionSets.RootItem.Children, expectedSet.Name);
                    SavedItem existingTopLevel = FindTopLevelSavedItemByName(document, expectedSet.Name);
                    if (namedCount == 0)
                    {
                        document.SelectionSets.AddCopy(newSet);
                        NativeSetChange createdChange = new NativeSetChange();
                        createdChange.Name = expectedSet.Name;
                        createdChange.WasReplacement = false;
                        createdChange.TargetItemCount = targetItems.Count;
                        changes.Add(createdChange);
                        if (!(FindTopLevelSavedItemByName(document, expectedSet.Name) is SelectionSet))
                            throw new InvalidOperationException("После добавления не найден верхнеуровневый Selection Set '" + expectedSet.Name + "'.");
                        createdSelectionSetCount++;
                    }
                    else
                    {
                        if (namedCount != 1 || !(existingTopLevel is SelectionSet))
                            throw new InvalidOperationException("Имя '" + expectedSet.Name + "' нельзя безопасно заменить: обнаружено совпадений=" + namedCount + ".");

                        SelectionSet backup = (existingTopLevel as SelectionSet).CreateCopy() as SelectionSet;
                        if (backup == null)
                            throw new InvalidOperationException("Не удалось создать резервную копию Selection Set '" + expectedSet.Name + "'.");

                        GroupItem rootParent = document.SelectionSets.RootItem;
                        int index = -1;
                        for (int i = 0; i < rootParent.Children.Count; i++)
                        {
                            SavedItem sibling = rootParent.Children[i];
                            if (sibling is SelectionSet && string.Equals(
                                sibling.DisplayName ?? "", expectedSet.Name, StringComparison.OrdinalIgnoreCase))
                            {
                                index = i;
                                break;
                            }
                        }
                        if (index < 0)
                            throw new InvalidOperationException("Не удалось определить позицию существующего Selection Set '" + expectedSet.Name + "'.");

                        NativeSetChange replacement = new NativeSetChange();
                        replacement.Name = expectedSet.Name;
                        replacement.WasReplacement = true;
                        replacement.Backup = backup;
                        replacement.Parent = rootParent;
                        replacement.Index = index;
                        replacement.TargetItemCount = targetItems.Count;
                        changes.Add(replacement);

                        document.SelectionSets.ReplaceWithCopy(rootParent, index, newSet);
                        if (!(FindTopLevelSavedItemByName(document, expectedSet.Name) is SelectionSet))
                            throw new InvalidOperationException("После замены не найден верхнеуровневый Selection Set '" + expectedSet.Name + "'.");
                        replacedSelectionSetCount++;
                    }
                    createdTargetModelItemCount += targetItems.Count;
                }
            }
            catch (Exception originalException)
            {
                List<string> rollbackProblems = new List<string>();
                for (int i = changes.Count - 1; i >= 0; i--)
                {
                    NativeSetChange change = changes[i];
                    try
                    {
                        SavedItem currentItem = FindTopLevelSavedItemByName(document, change.Name);
                        if (change.WasReplacement)
                        {
                            if (change.Parent == null || change.Index < 0 || change.Backup == null)
                                throw new InvalidOperationException("Нет данных резервной копии/позиции.");
                            // Restoring a replacement does not change the collection size, so the
                            // original index remains valid after later operations were rolled back.
                            document.SelectionSets.ReplaceWithCopy(change.Parent, change.Index, change.Backup);
                        }
                        else if (currentItem != null && !document.SelectionSets.Remove(currentItem))
                        {
                            throw new InvalidOperationException("Remove вернул false.");
                        }
                    }
                    catch (Exception rollbackException)
                    {
                        rollbackProblems.Add("'" + change.Name + "': " + rollbackException.Message);
                    }
                }

                createdSelectionSetCount = 0;
                replacedSelectionSetCount = 0;
                createdTargetModelItemCount = 0;
                if (rollbackProblems.Count > 0)
                {
                    throw new InvalidOperationException(
                        "Нативная пересборка завершилась ошибкой. При попытке отката возникли проблемы:\n" +
                        string.Join(Environment.NewLine, rollbackProblems.ToArray()) +
                        "\nИсходная ошибка: " + originalException.Message,
                        originalException);
                }

                throw new InvalidOperationException(
                    "Нативная пересборка завершилась ошибкой; все изменения этой операции отменены.\n" +
                    originalException.Message,
                    originalException);
            }
        }

        private static ExpectedSet CreateWholeRootExpectedSet(
            Document document,
            string targetRootDisplayName,
            string targetRootFileName,
            string setName)
        {
            ExpectedSet expectedSet = new ExpectedSet();
            expectedSet.Name = setName;
            expectedSet.GeneratedFromUnmappedTargetRoot = true;
            expectedSet.GeneratedTargetModelFileName = targetRootFileName;

            string documentRootDisplayName = "";
            if (document != null && document.Models != null)
            {
                foreach (Model model in document.Models)
                {
                    if (model != null && model.RootItem != null)
                    {
                        documentRootDisplayName = SafeDisplayName(model.RootItem);
                        if (!string.IsNullOrWhiteSpace(documentRootDisplayName))
                            break;
                    }
                }
            }

            ExpectedItem expectedItem = new ExpectedItem();
            expectedItem.DisplayName = targetRootDisplayName;
            expectedItem.SourceHierarchyPath = documentRootDisplayName + " / " + targetRootDisplayName;
            expectedItem.HierarchyNodes.Add(new Dictionary<string, object>
            {
                { "displayName", documentRootDisplayName },
                { "properties", new List<object>() }
            });
            expectedItem.HierarchyNodes.Add(new Dictionary<string, object>
            {
                { "displayName", targetRootDisplayName },
                { "properties", new List<object>() }
            });
            expectedSet.Items.Add(expectedItem);
            return expectedSet;
        }

        private static Dictionary<string, object> BuildGeneratedRootMappingReport(
            string targetRootDisplayName,
            string targetModelFileName,
            string generatedSetName,
            string status,
            int targetRootCount)
        {
            Dictionary<string, object> report = new Dictionary<string, object>();
            report["sourceRootName"] = targetRootDisplayName;
            report["mappingApplied"] = false;
            report["lookupMode"] = "unmapped-target-root-auto-set";
            report["targetModelFileName"] = targetModelFileName;
            report["targetRootNamesFound"] = new List<string> { targetRootDisplayName };
            report["targetRootCount"] = targetRootCount;
            report["mappingStatus"] = status;
            report["generatedSelectionSetName"] = generatedSetName;
            report["generatedFromUnmappedTargetRoot"] = true;
            return report;
        }

        // Optional configuration is stored as rootOverrides.json next to the selected
        // source JSON. Keys and values are linked RVT file names, not Navisworks display
        // names, because Navisworks appends a position suffix (e.g. ": 9 : position").
        private static Dictionary<string, string> LoadRootOverrides(
            string configPath,
            JavaScriptSerializer serializer)
        {
            Dictionary<string, string> overrides =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
                return overrides;

            if (serializer == null)
                throw new ArgumentNullException("serializer");

            object parsed = serializer.DeserializeObject(File.ReadAllText(configPath, Encoding.UTF8));
            Dictionary<string, object> config = AsDictionary(parsed);
            if (config == null)
                throw new InvalidDataException("В корне файла ожидается JSON-объект.");

            int formatVersion = 0;
            object versionValue = GetDictionaryValue(config, "formatVersion");
            if (versionValue == null ||
                !int.TryParse(Convert.ToString(versionValue, CultureInfo.InvariantCulture), out formatVersion) ||
                formatVersion != 1)
            {
                throw new InvalidDataException("Ожидается formatVersion = 1.");
            }

            object overridesValue = GetDictionaryValue(config, "overrides");
            if (overridesValue == null)
                throw new InvalidDataException("Не найден массив 'overrides'.");

            List<object> rawOverrides = AsObjectList(overridesValue);
            HashSet<string> targetFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (object rawOverride in rawOverrides)
            {
                Dictionary<string, object> overrideItem = AsDictionary(rawOverride);
                if (overrideItem == null)
                    throw new InvalidDataException("Каждый элемент 'overrides' должен быть JSON-объектом.");

                string sourceFileName = ExtractModelFileName(
                    GetDictionaryString(overrideItem, "sourceModelFileName"));
                string targetFileName = ExtractModelFileName(
                    GetDictionaryString(overrideItem, "targetModelFileName"));

                if (string.IsNullOrWhiteSpace(sourceFileName) ||
                    string.IsNullOrWhiteSpace(targetFileName))
                {
                    throw new InvalidDataException(
                        "У каждого правила обязательны sourceModelFileName и targetModelFileName с расширением .rvt.");
                }

                if (overrides.ContainsKey(sourceFileName))
                    throw new InvalidDataException("Повторный sourceModelFileName: " + sourceFileName);

                if (!targetFileNames.Add(targetFileName))
                {
                    throw new InvalidDataException(
                        "Несколько исходных моделей назначены на одну целевую модель: " + targetFileName);
                }

                overrides.Add(sourceFileName, targetFileName);
            }

            return overrides;
        }

        private static string GetMappedTargetRootFileName(
            string sourceRootDisplayName,
            Dictionary<string, string> rootOverrides)
        {
            if (rootOverrides == null || string.IsNullOrWhiteSpace(sourceRootDisplayName))
                return "";

            string sourceFileName = ExtractModelFileName(sourceRootDisplayName);
            string targetFileName;
            return rootOverrides.TryGetValue(sourceFileName, out targetFileName)
                ? targetFileName
                : "";
        }

        // Navisworks appends a model-position label after the linked RVT filename,
        // e.g. "...ОВ1_П3_R23.rvt : 9 : позиция ...". Extract only the .rvt filename.
        private static string ExtractModelFileName(string displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName))
                return "";

            int extensionIndex = displayName.IndexOf(".rvt", StringComparison.OrdinalIgnoreCase);
            if (extensionIndex < 0)
                return "";

            string filePathPrefix = displayName.Substring(0, extensionIndex + 4).Trim();
            int lastSlash = filePathPrefix.LastIndexOf('/');
            int lastBackslash = filePathPrefix.LastIndexOf('\\');
            int lastSeparator = Math.Max(lastSlash, lastBackslash);
            if (lastSeparator >= 0 && lastSeparator < filePathPrefix.Length - 1)
                filePathPrefix = filePathPrefix.Substring(lastSeparator + 1);

            return filePathPrefix.Trim();
        }

        private static List<ModelItem> FindStartingNodesByModelFileName(
            Document document,
            string requiredModelFileName,
            ref int nodesInspected)
        {
            List<ModelItem> matches = new List<ModelItem>();
            if (document == null || document.Models == null ||
                string.IsNullOrWhiteSpace(requiredModelFileName))
            {
                return matches;
            }

            foreach (Model model in document.Models)
            {
                if (model == null || model.RootItem == null)
                    continue;

                matches.AddRange(FindStartingNodesByModelFileName(
                    model.RootItem,
                    requiredModelFileName,
                    ref nodesInspected));
            }

            return matches;
        }

        private static List<ModelItem> FindStartingNodesByModelFileName(
            ModelItem documentRoot,
            string requiredModelFileName,
            ref int nodesInspected)
        {
            List<ModelItem> currentLayer = new List<ModelItem>();
            if (documentRoot == null || string.IsNullOrWhiteSpace(requiredModelFileName))
                return currentLayer;

            currentLayer.Add(documentRoot);

            for (int depth = 0; depth <= MaxRootSearchDepth; depth++)
            {
                List<ModelItem> matches = new List<ModelItem>();
                foreach (ModelItem item in currentLayer)
                {
                    CountInspectedNode(ref nodesInspected);
                    string itemFileName = ExtractModelFileName(SafeDisplayName(item));
                    if (string.Equals(
                        itemFileName,
                        requiredModelFileName,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        matches.Add(item);
                    }
                }

                if (matches.Count > 0)
                    return matches;

                if (depth == MaxRootSearchDepth)
                    break;

                List<ModelItem> nextLayer = new List<ModelItem>();
                foreach (ModelItem item in currentLayer)
                    nextLayer.AddRange(GetChildrenSafely(item, ref nodesInspected));

                currentLayer = nextLayer;
                if (currentLayer.Count == 0)
                    break;
            }

            return new List<ModelItem>();
        }

        private static List<ModelItem> FindStartingNodes(
            Document document,
            string requiredName,
            ref int nodesInspected)
        {
            List<ModelItem> matches = new List<ModelItem>();
            if (document == null || document.Models == null || string.IsNullOrEmpty(requiredName))
                return matches;

            foreach (Model model in document.Models)
            {
                if (model == null || model.RootItem == null)
                    continue;

                matches.AddRange(FindStartingNodes(model.RootItem, requiredName, ref nodesInspected));
            }

            return matches;
        }

        private static List<ModelItem> FindStartingNodes(
            ModelItem documentRoot,
            string requiredName,
            ref int nodesInspected)
        {
            List<ModelItem> currentLayer = new List<ModelItem>();
            if (documentRoot == null || string.IsNullOrEmpty(requiredName))
                return currentLayer;

            currentLayer.Add(documentRoot);

            for (int depth = 0; depth <= MaxRootSearchDepth; depth++)
            {
                List<ModelItem> matches = new List<ModelItem>();
                foreach (ModelItem item in currentLayer)
                {
                    CountInspectedNode(ref nodesInspected);
                    if (string.Equals(SafeDisplayName(item), requiredName, StringComparison.Ordinal))
                        matches.Add(item);
                }

                if (matches.Count > 0)
                    return matches;

                if (depth == MaxRootSearchDepth)
                    break;

                List<ModelItem> nextLayer = new List<ModelItem>();
                foreach (ModelItem item in currentLayer)
                    nextLayer.AddRange(GetChildrenSafely(item, ref nodesInspected));

                currentLayer = nextLayer;
                if (currentLayer.Count == 0)
                    break;
            }

            return new List<ModelItem>();
        }

        private static TargetNodeInfo CreateTargetRoot(
            ModelItem rootItem,
            ref int nodesInspected,
            ref int targetNodesIndexed)
        {
            if (rootItem == null)
                return null;

            CountInspectedNode(ref nodesInspected);
            targetNodesIndexed++;

            TargetNodeInfo root = new TargetNodeInfo();
            root.Item = rootItem;
            root.DisplayName = SafeDisplayName(rootItem);
            root.Parent = null;
            root.Depth = 0;
            root.ChildrenLoaded = false;
            return root;
        }

        private static List<List<TargetNodeInfo>> FindPathsByHierarchy(
            Dictionary<string, List<TargetNodeInfo>> indexedRootsByName,
            List<string> pathSegments,
            Dictionary<TargetNodeInfo, Dictionary<string, List<TargetNodeInfo>>> descendantMatchCache,
            ref int nodesInspected,
            ref int targetNodesIndexed,
            ref int targetPathSearchVisits)
        {
            List<List<TargetNodeInfo>> completedPaths = new List<List<TargetNodeInfo>>();
            if (indexedRootsByName == null || pathSegments == null || pathSegments.Count == 0)
                return completedPaths;

            List<TargetNodeInfo> startingNodes;
            if (!indexedRootsByName.TryGetValue(pathSegments[0], out startingNodes))
                return completedPaths;

            List<List<TargetNodeInfo>> activePaths = new List<List<TargetNodeInfo>>();
            foreach (TargetNodeInfo startNode in startingNodes)
            {
                if (startNode == null)
                    continue;

                List<TargetNodeInfo> path = new List<TargetNodeInfo>();
                path.Add(startNode);
                activePaths.Add(path);
            }

            if (activePaths.Count == 0)
                return completedPaths;

            for (int segmentIndex = 1; segmentIndex < pathSegments.Count; segmentIndex++)
            {
                List<List<TargetNodeInfo>> nextPaths = new List<List<TargetNodeInfo>>();
                string requiredName = pathSegments[segmentIndex];

                foreach (List<TargetNodeInfo> activePath in activePaths)
                {
                    TargetNodeInfo parent = activePath[activePath.Count - 1];
                    List<TargetNodeInfo> segmentMatches = FindNearestDescendantMatches(
                        parent,
                        requiredName,
                        descendantMatchCache,
                        ref nodesInspected,
                        ref targetNodesIndexed,
                        ref targetPathSearchVisits);

                    foreach (TargetNodeInfo match in segmentMatches)
                    {
                        List<TargetNodeInfo> nextPath = new List<TargetNodeInfo>(activePath);
                        nextPath.Add(match);
                        nextPaths.Add(nextPath);

                        if (nextPaths.Count > MaxPathCandidates)
                            throw new SearchLimitException();
                    }
                }

                activePaths = nextPaths;
                if (activePaths.Count == 0)
                    return completedPaths;
            }

            return activePaths;
        }

        private static List<TargetNodeInfo> FindNearestDescendantMatches(
            TargetNodeInfo parent,
            string requiredName,
            Dictionary<TargetNodeInfo, Dictionary<string, List<TargetNodeInfo>>> descendantMatchCache,
            ref int nodesInspected,
            ref int targetNodesIndexed,
            ref int targetPathSearchVisits)
        {
            List<TargetNodeInfo> noMatches = new List<TargetNodeInfo>();
            if (parent == null || string.IsNullOrEmpty(requiredName))
                return noMatches;

            Dictionary<string, List<TargetNodeInfo>> matchesByName;
            if (descendantMatchCache.TryGetValue(parent, out matchesByName))
            {
                List<TargetNodeInfo> cachedMatches;
                if (matchesByName.TryGetValue(requiredName, out cachedMatches))
                    return cachedMatches;
            }
            else
            {
                matchesByName = new Dictionary<string, List<TargetNodeInfo>>(StringComparer.Ordinal);
                descendantMatchCache.Add(parent, matchesByName);
            }

            // Stop at the nearest descendant level containing the requested name.
            // All matches at that level are retained so following path segments can
            // disambiguate them; duplicate intermediate category names are expected.
            List<TargetNodeInfo> currentLayer = GetIndexedChildren(
                parent,
                ref nodesInspected,
                ref targetNodesIndexed);

            for (int depth = 1; depth <= MaxIntermediateSearchDepth; depth++)
            {
                List<TargetNodeInfo> matches = new List<TargetNodeInfo>();
                foreach (TargetNodeInfo item in currentLayer)
                {
                    targetPathSearchVisits++;
                    if (targetPathSearchVisits > MaxPathSearchVisits)
                        throw new SearchLimitException();

                    if (string.Equals(item.DisplayName, requiredName, StringComparison.Ordinal))
                        matches.Add(item);
                }

                if (matches.Count > 0)
                {
                    matchesByName[requiredName] = matches;
                    return matches;
                }

                if (depth == MaxIntermediateSearchDepth)
                    break;

                List<TargetNodeInfo> nextLayer = new List<TargetNodeInfo>();
                foreach (TargetNodeInfo item in currentLayer)
                {
                    // The target model repeats hierarchy categories under levels, but
                    // geometry/object branches can be very large. We still inspect a
                    // node for a name match above; only category nodes are expanded
                    // further as hierarchy containers. The starting linked-model root
                    // is always expanded by the initial GetIndexedChildren call.
                    if (!CanExpandForHierarchySearch(item))
                        continue;

                    List<TargetNodeInfo> children = GetIndexedChildren(
                        item,
                        ref nodesInspected,
                        ref targetNodesIndexed);

                    foreach (TargetNodeInfo child in children)
                    {
                        // Count a path-search visit only when the node is actually
                        // inspected for a name match in the next iteration. Counting
                        // both enqueue and inspection double-counted the same node.
                        nextLayer.Add(child);
                    }
                }

                currentLayer = nextLayer;
                if (currentLayer.Count == 0)
                    break;
            }

            matchesByName[requiredName] = noMatches;
            return noMatches;
        }

        private static List<List<TargetNodeInfo>> ResolveCandidatePathsByIdentity(
            ExpectedItem expectedItem,
            List<List<TargetNodeInfo>> candidatePaths)
        {
            if (expectedItem == null || candidatePaths == null || candidatePaths.Count < 2)
                return candidatePaths ?? new List<List<TargetNodeInfo>>();

            List<IdentityValue> sourceIdentityValues = GetSourceTerminalIdentityValues(expectedItem);
            if (sourceIdentityValues.Count == 0)
                return candidatePaths;

            int highestScore = 0;
            List<List<TargetNodeInfo>> bestPaths = new List<List<TargetNodeInfo>>();

            foreach (List<TargetNodeInfo> candidatePath in candidatePaths)
            {
                if (candidatePath == null || candidatePath.Count == 0)
                    continue;

                TargetNodeInfo terminalNode = candidatePath[candidatePath.Count - 1];
                if (terminalNode == null || terminalNode.Item == null)
                    continue;

                int score = 0;
                foreach (IdentityValue identityValue in sourceIdentityValues)
                {
                    string targetValue = GetModelItemPropertyValue(
                        terminalNode.Item,
                        identityValue.CategoryDisplayName,
                        identityValue.PropertyDisplayName);

                    if (!string.IsNullOrWhiteSpace(targetValue) &&
                        string.Equals(identityValue.Value, targetValue, StringComparison.OrdinalIgnoreCase))
                    {
                        score++;
                    }
                }

                if (score > highestScore)
                {
                    highestScore = score;
                    bestPaths.Clear();
                    bestPaths.Add(candidatePath);
                }
                else if (score == highestScore && score > 0)
                {
                    bestPaths.Add(candidatePath);
                }
            }

            // Only reduce the candidates when identity evidence uniquely identifies
            // one path. Ties and zero matches remain explicitly ambiguous.
            if (highestScore > 0 && bestPaths.Count == 1)
                return bestPaths;

            return candidatePaths;
        }

        private static List<IdentityValue> GetSourceTerminalIdentityValues(ExpectedItem expectedItem)
        {
            List<IdentityValue> result = new List<IdentityValue>();
            if (expectedItem == null || expectedItem.HierarchyNodes == null ||
                expectedItem.HierarchyNodes.Count == 0)
            {
                return result;
            }

            Dictionary<string, object> terminalSourceNode =
                expectedItem.HierarchyNodes[expectedItem.HierarchyNodes.Count - 1];
            List<object> sourceProperties = AsObjectList(
                GetDictionaryValue(terminalSourceNode, "properties"));

            foreach (object rawProperty in sourceProperties)
            {
                Dictionary<string, object> propertyDictionary = AsDictionary(rawProperty);
                if (propertyDictionary == null)
                    continue;

                string propertyDisplayName = GetDictionaryString(propertyDictionary, "propertyDisplayName");
                if (!IsIdentityProperty(propertyDisplayName))
                    continue;

                string value = NormalizeDisplayValue(GetDictionaryString(propertyDictionary, "value"));
                if (!IsUsableIdentityValue(propertyDisplayName, value))
                    continue;

                IdentityValue identityValue = new IdentityValue();
                identityValue.CategoryDisplayName = GetDictionaryString(propertyDictionary, "categoryDisplayName");
                identityValue.PropertyDisplayName = propertyDisplayName;
                identityValue.Value = value.Trim();
                result.Add(identityValue);
            }

            return result;
        }

        private static bool IsUsableIdentityValue(string propertyDisplayName, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            string normalizedValue = value.Trim();
            if (propertyDisplayName.IndexOf("Guid", StringComparison.OrdinalIgnoreCase) >= 0 ||
                propertyDisplayName.IndexOf("GUID", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Guid parsedGuid;
                if (Guid.TryParse(normalizedValue, out parsedGuid) && parsedGuid == Guid.Empty)
                    return false;
            }

            return true;
        }

        private static bool IsExpectedTerminalCategory(ExpectedItem expectedItem)
        {
            string sourceType = GetExpectedTerminalNodeType(expectedItem);
            return string.Equals(sourceType, "Категория", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(sourceType, "Category", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetExpectedTerminalNodeType(ExpectedItem expectedItem)
        {
            if (expectedItem == null || expectedItem.HierarchyNodes == null ||
                expectedItem.HierarchyNodes.Count == 0)
            {
                return "";
            }

            Dictionary<string, object> terminalSourceNode =
                expectedItem.HierarchyNodes[expectedItem.HierarchyNodes.Count - 1];
            List<object> sourceProperties = AsObjectList(
                GetDictionaryValue(terminalSourceNode, "properties"));

            foreach (object rawProperty in sourceProperties)
            {
                Dictionary<string, object> propertyDictionary = AsDictionary(rawProperty);
                if (propertyDictionary == null)
                    continue;

                string categoryName = GetDictionaryString(propertyDictionary, "categoryDisplayName");
                string propertyName = GetDictionaryString(propertyDictionary, "propertyDisplayName");
                if (string.Equals(categoryName, "Элемент", StringComparison.Ordinal) &&
                    string.Equals(propertyName, "Тип", StringComparison.Ordinal))
                {
                    return NormalizeDisplayValue(GetDictionaryString(propertyDictionary, "value"));
                }
            }

            return "";
        }

        private static bool AllCandidatePathsTerminateInCategories(
            List<List<TargetNodeInfo>> candidatePaths)
        {
            if (candidatePaths == null || candidatePaths.Count == 0)
                return false;

            foreach (List<TargetNodeInfo> candidatePath in candidatePaths)
            {
                if (candidatePath == null || candidatePath.Count == 0)
                    return false;

                TargetNodeInfo terminalNode = candidatePath[candidatePath.Count - 1];
                string targetType = GetTargetElementType(terminalNode);

                if (!IsCategoryType(targetType))
                {
                    return false;
                }
            }

            return true;
        }

        private static void AddTargetHierarchyPaths(
            List<List<TargetNodeInfo>> candidatePaths,
            List<string> targetHierarchyPaths)
        {
            if (candidatePaths == null || targetHierarchyPaths == null)
                return;

            foreach (List<TargetNodeInfo> candidatePath in candidatePaths)
            {
                if (candidatePath == null || candidatePath.Count == 0)
                    continue;

                TargetNodeInfo terminalNode = candidatePath[candidatePath.Count - 1];
                targetHierarchyPaths.Add(BuildActualHierarchyPath(terminalNode));
            }
        }

        private static bool AllCandidatePathsShareTargetRoot(
            List<List<TargetNodeInfo>> candidatePaths)
        {
            if (candidatePaths == null || candidatePaths.Count == 0)
                return false;

            TargetNodeInfo expectedRoot = null;
            foreach (List<TargetNodeInfo> candidatePath in candidatePaths)
            {
                if (candidatePath == null || candidatePath.Count == 0)
                    return false;

                TargetNodeInfo terminalNode = candidatePath[candidatePath.Count - 1];
                TargetNodeInfo root = GetIndexedRoot(terminalNode);
                if (root == null)
                    return false;

                if (expectedRoot == null)
                    expectedRoot = root;
                else if (!object.ReferenceEquals(expectedRoot, root))
                    return false;
            }

            return true;
        }

        private static TargetNodeInfo GetIndexedRoot(TargetNodeInfo node)
        {
            TargetNodeInfo current = node;
            while (current != null && current.Parent != null)
                current = current.Parent;
            return current;
        }

        private static bool TryClassifyNativePolicyRepeatedCandidates(
            ExpectedItem expectedItem,
            List<List<TargetNodeInfo>> candidatePaths,
            out string reasonCode)
        {
            reasonCode = "NATIVE_POLICY_FANOUT_NOT_SAFE";
            if (expectedItem == null || candidatePaths == null || candidatePaths.Count < 2)
            {
                reasonCode = "NATIVE_POLICY_FANOUT_REQUIRES_MULTIPLE_CANDIDATES";
                return false;
            }

            string sourceTerminalType = GetExpectedTerminalNodeType(expectedItem);
            bool expectedFamily = string.Equals(sourceTerminalType, "Семейство", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(sourceTerminalType, "Family", StringComparison.OrdinalIgnoreCase);
            bool expectedType = string.Equals(sourceTerminalType, "Тип", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(sourceTerminalType, "Type", StringComparison.OrdinalIgnoreCase);
            if (!expectedFamily && !expectedType)
            {
                reasonCode = "NATIVE_POLICY_FANOUT_ONLY_SUPPORTS_FAMILY_OR_TYPE_NODES";
                return false;
            }

            if (!AllCandidatePathsShareTargetRoot(candidatePaths))
            {
                reasonCode = "NATIVE_POLICY_CANDIDATES_SPAN_MULTIPLE_LINKED_ROOTS";
                return false;
            }

            string repeatReason;
            if (!AllCandidatePathsRepeatAcrossLevels(candidatePaths, out repeatReason))
            {
                reasonCode = "NATIVE_POLICY_" + repeatReason;
                return false;
            }

            foreach (List<TargetNodeInfo> candidatePath in candidatePaths)
            {
                if (candidatePath == null || candidatePath.Count == 0)
                {
                    reasonCode = "NATIVE_POLICY_EMPTY_CANDIDATE_PATH";
                    return false;
                }

                TargetNodeInfo terminal = candidatePath[candidatePath.Count - 1];
                string targetType = GetTargetElementType(terminal);
                bool targetIsFamily = string.Equals(targetType, "Семейство", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(targetType, "Family", StringComparison.OrdinalIgnoreCase);
                bool targetIsType = string.Equals(targetType, "Тип", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(targetType, "Type", StringComparison.OrdinalIgnoreCase);

                if ((expectedFamily && !targetIsFamily) || (expectedType && !targetIsType))
                {
                    reasonCode = "NATIVE_POLICY_TARGET_TERMINAL_TYPE_MISMATCH";
                    return false;
                }
            }

            // Unlike legacy transfer, native policy has no source element Id/GUID to compare.
            // The exact hierarchy path plus a single mapped root and distinct repeated levels
            // is the intended semantic rule for Family/Type selection in the policy.
            reasonCode = expectedFamily
                ? "NATIVE_POLICY_FAMILY_PATH_REPEATS_ON_DISTINCT_LEVELS"
                : "NATIVE_POLICY_TYPE_PATH_REPEATS_ON_DISTINCT_LEVELS";
            return true;
        }

        private static bool TryClassifyRepeatedCandidates(
            ExpectedItem expectedItem,
            List<List<TargetNodeInfo>> candidatePaths,
            out string status,
            out string reasonCode)
        {
            status = "AMBIGUOUS";
            reasonCode = "NO_SAFE_ONE_TO_MANY_RULE_APPLIED";

            if (candidatePaths == null || candidatePaths.Count < 2)
            {
                reasonCode = "FEWER_THAN_TWO_CANDIDATES";
                return false;
            }

            if (!AllCandidatePathsShareTargetRoot(candidatePaths))
            {
                reasonCode = "CANDIDATES_SPAN_MULTIPLE_TARGET_ROOTS";
                return false;
            }

            if (!AllCandidatePathsRepeatAcrossLevels(candidatePaths, out reasonCode))
                return false;

            List<IdentityValue> sourceIdentityValues = GetSourceTerminalIdentityValues(expectedItem);
            if (sourceIdentityValues.Count > 0)
            {
                // GUID is not mandatory. Require every usable source identity
                // property (for example, Revit Id) to match every candidate.
                // The earlier checks still require one target root, distinct
                // LcRevitLayer levels and the same canonical path.
                if (!AllCandidatesMatchSourceIdentity(candidatePaths, sourceIdentityValues))
                {
                    reasonCode = "NOT_ALL_CANDIDATES_MATCH_SOURCE_IDENTITY";
                    return false;
                }

                status = "MATCHED_ONE_TO_MANY_IDENTITY";
                reasonCode = "ALL_SOURCE_IDENTITY_PROPERTIES_MATCH_EVERY_CANDIDATE_AND_REPEAT_ACROSS_DISTINCT_LEVELS";
                return true;
            }

            // Structural fan-out is only for identity-less Family/container nodes,
            // not arbitrary geometry or instances. In this source export the node
            // "Типы трубопроводов" is explicitly typed as "Семейство".
            string sourceTerminalType = GetExpectedTerminalNodeType(expectedItem);
            if (!string.Equals(sourceTerminalType, "Семейство", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(sourceTerminalType, "Family", StringComparison.OrdinalIgnoreCase))
            {
                reasonCode = "STRUCTURAL_FANOUT_REQUIRES_SOURCE_FAMILY_CONTAINER";
                return false;
            }

            // If target properties are unreadable, do not infer that identity is absent.
            foreach (List<TargetNodeInfo> candidatePath in candidatePaths)
            {
                TargetNodeInfo terminalNode = candidatePath[candidatePath.Count - 1];
                string targetTerminalType = GetTargetElementType(terminalNode);
                if (!string.Equals(targetTerminalType, "Семейство", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(targetTerminalType, "Family", StringComparison.OrdinalIgnoreCase))
                {
                    reasonCode = "STRUCTURAL_FANOUT_REQUIRES_TARGET_FAMILY_CONTAINER";
                    return false;
                }

                if (!TargetHasNoReadableIdentityValues(terminalNode.Item))
                {
                    reasonCode = "SOURCE_HAS_NO_IDENTITY_BUT_TARGET_IDENTITY_EXISTS_OR_IS_UNREADABLE";
                    return false;
                }
            }

            status = "MATCHED_ONE_TO_MANY_STRUCTURAL";
            reasonCode = "IDENTITYLESS_CONTAINERS_SHARE_CANONICAL_PATH_ACROSS_DISTINCT_LEVELS";
            return true;
        }

        private static bool AllCandidatesMatchSourceIdentity(
            List<List<TargetNodeInfo>> candidatePaths,
            List<IdentityValue> sourceIdentityValues)
        {
            if (candidatePaths == null || candidatePaths.Count < 2 ||
                sourceIdentityValues == null || sourceIdentityValues.Count == 0)
            {
                return false;
            }

            foreach (List<TargetNodeInfo> candidatePath in candidatePaths)
            {
                if (candidatePath == null || candidatePath.Count == 0)
                    return false;

                TargetNodeInfo terminalNode = candidatePath[candidatePath.Count - 1];
                if (terminalNode == null || terminalNode.Item == null)
                    return false;

                foreach (IdentityValue identityValue in sourceIdentityValues)
                {
                    string targetValue = GetModelItemPropertyValue(
                        terminalNode.Item,
                        identityValue.CategoryDisplayName,
                        identityValue.PropertyDisplayName);

                    if (string.IsNullOrWhiteSpace(targetValue) ||
                        !string.Equals(
                            NormalizeDisplayValue(identityValue.Value),
                            NormalizeDisplayValue(targetValue),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool AllCandidatePathsRepeatAcrossLevels(
            List<List<TargetNodeInfo>> candidatePaths,
            out string reasonCode)
        {
            reasonCode = "CANDIDATES_DO_NOT_REPEAT_ACROSS_LEVELS";
            if (candidatePaths == null || candidatePaths.Count < 2)
                return false;

            string expectedCanonicalPath = null;
            HashSet<TargetNodeInfo> seenLevels = new HashSet<TargetNodeInfo>();

            foreach (List<TargetNodeInfo> candidatePath in candidatePaths)
            {
                if (candidatePath == null || candidatePath.Count == 0)
                {
                    reasonCode = "EMPTY_CANDIDATE_PATH";
                    return false;
                }

                TargetNodeInfo terminalNode = candidatePath[candidatePath.Count - 1];
                TargetNodeInfo levelNode = GetNearestLevelAncestor(terminalNode);
                if (levelNode == null)
                {
                    reasonCode = "CANDIDATE_WITHOUT_LCREVITLAYER_ANCESTOR";
                    return false;
                }

                if (!seenLevels.Add(levelNode))
                {
                    reasonCode = "MULTIPLE_CANDIDATES_OCCUR_UNDER_THE_SAME_LEVEL";
                    return false;
                }

                string canonicalPath = BuildCanonicalPathIgnoringLevels(terminalNode);
                if (string.IsNullOrWhiteSpace(canonicalPath))
                {
                    reasonCode = "CANONICAL_TARGET_PATH_IS_EMPTY";
                    return false;
                }

                if (expectedCanonicalPath == null)
                    expectedCanonicalPath = canonicalPath;
                else if (!string.Equals(
                    expectedCanonicalPath,
                    canonicalPath,
                    StringComparison.Ordinal))
                {
                    reasonCode = "CANDIDATE_PATHS_DIFFER_AFTER_LEVELS_ARE_EXCLUDED";
                    return false;
                }
            }

            reasonCode = "REPEATED_ACROSS_DISTINCT_LEVELS_WITH_IDENTICAL_CANONICAL_PATH";
            return true;
        }

        private static TargetNodeInfo GetNearestLevelAncestor(TargetNodeInfo node)
        {
            TargetNodeInfo current = node == null ? null : node.Parent;
            while (current != null)
            {
                if (IsLevelNode(current))
                    return current;
                current = current.Parent;
            }

            return null;
        }

        private static bool IsLevelNode(TargetNodeInfo node)
        {
            if (node == null || node.Item == null)
                return false;

            return string.Equals(
                SafeModelItemClassName(node.Item),
                "LcRevitLayer",
                StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildCanonicalPathIgnoringLevels(TargetNodeInfo node)
        {
            List<string> reversedNames = new List<string>();
            TargetNodeInfo current = node;
            while (current != null)
            {
                if (!IsLevelNode(current) && !string.IsNullOrWhiteSpace(current.DisplayName))
                    reversedNames.Add(current.DisplayName);
                current = current.Parent;
            }

            StringBuilder builder = new StringBuilder();
            for (int index = reversedNames.Count - 1; index >= 0; index--)
            {
                if (builder.Length > 0)
                    builder.Append(" / ");
                builder.Append(reversedNames[index]);
            }

            return builder.ToString();
        }

        private static bool TargetHasNoReadableIdentityValues(ModelItem modelItem)
        {
            if (modelItem == null)
                return false;

            try
            {
                foreach (PropertyCategory category in modelItem.PropertyCategories)
                {
                    foreach (DataProperty property in category.Properties)
                    {
                        if (!IsIdentityProperty(property.DisplayName))
                            continue;

                        if (!string.IsNullOrWhiteSpace(GetPropertyValueText(property)))
                            return false;
                    }
                }
            }
            catch
            {
                // Property access failures are not evidence that an identity is absent.
                return false;
            }

            return true;
        }

        private static List<object> BuildSourceIdentityValueReports(ExpectedItem expectedItem)
        {
            List<object> reports = new List<object>();
            foreach (IdentityValue identityValue in GetSourceTerminalIdentityValues(expectedItem))
            {
                Dictionary<string, object> report = new Dictionary<string, object>();
                report["categoryDisplayName"] = identityValue.CategoryDisplayName;
                report["propertyDisplayName"] = identityValue.PropertyDisplayName;
                report["value"] = identityValue.Value;
                reports.Add(report);
            }
            return reports;
        }

        private static bool CanExpandForHierarchySearch(TargetNodeInfo node)
        {
            if (node == null || node.Item == null)
                return false;

            // Depth zero represents the linked-model root located by name. Always
            // expand it to expose the first hierarchy layer.
            if (node.Depth == 0)
                return true;

            // IMPORTANT: ModelItem's class and the Revit "Element -> Type"
            // property are different concepts. For a Revit level, ClassName is
            // "LcRevitLayer", while Element -> Type may contain a type name such
            // as "Уровни: Уровень: ...". Do not detect a structural layer by that
            // Revit property; use the Navisworks ModelItem class instead.
            try
            {
                if (string.Equals(
                    node.Item.ClassName,
                    "LcRevitLayer",
                    StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch
            {
                // If ClassName is unavailable, continue with the category test below.
            }

            // Category nodes still use the exported Element -> Type property, which
            // was verified in the debugger to return "Категория" for these nodes.
            return IsCategoryType(GetTargetElementType(node));
        }

        private static string GetTargetElementType(TargetNodeInfo node)
        {
            if (node == null || node.Item == null)
                return "";

            if (!node.ElementTypeLoaded)
            {
                node.ElementType = GetModelItemPropertyValue(node.Item, "Элемент", "Тип");
                node.ElementTypeLoaded = true;
            }

            return node.ElementType ?? "";
        }

        private static bool IsCategoryType(string value)
        {
            return string.Equals(value, "Категория", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "Category", StringComparison.OrdinalIgnoreCase);
        }

        private static List<TargetNodeInfo> GetIndexedChildren(
            TargetNodeInfo parent,
            ref int nodesInspected,
            ref int targetNodesIndexed)
        {
            if (parent == null)
                return new List<TargetNodeInfo>();

            if (parent.ChildrenLoaded)
                return parent.Children;

            List<TargetNodeInfo> loadedChildren = new List<TargetNodeInfo>();
            foreach (ModelItem childItem in parent.Item.Children)
            {
                CountInspectedNode(ref nodesInspected);

                TargetNodeInfo child = new TargetNodeInfo();
                child.Item = childItem;
                child.DisplayName = SafeDisplayName(childItem);
                child.Parent = parent;
                child.Depth = parent.Depth + 1;
                child.ChildrenLoaded = false;
                loadedChildren.Add(child);
            }

            parent.Children.AddRange(loadedChildren);
            parent.ChildrenLoaded = true;
            targetNodesIndexed += loadedChildren.Count;
            return parent.Children;
        }

        private static List<ModelItem> ConvertToModelItems(List<TargetNodeInfo> targetPathNodes)
        {
            List<ModelItem> result = new List<ModelItem>();
            if (targetPathNodes == null)
                return result;

            foreach (TargetNodeInfo node in targetPathNodes)
            {
                if (node != null && node.Item != null)
                    result.Add(node.Item);
            }

            return result;
        }

        private static List<object> BuildCandidatePathReports(
            List<List<TargetNodeInfo>> candidatePaths)
        {
            List<object> reports = new List<object>();
            if (candidatePaths == null)
                return reports;

            int itemCount = Math.Min(candidatePaths.Count, MaxCandidatePathsInReport);
            for (int index = 0; index < itemCount; index++)
            {
                List<TargetNodeInfo> candidatePath = candidatePaths[index];
                if (candidatePath == null || candidatePath.Count == 0)
                    continue;

                TargetNodeInfo finalNode = candidatePath[candidatePath.Count - 1];
                Dictionary<string, object> candidateReport = new Dictionary<string, object>();
                candidateReport["matchedPath"] = BuildDisplayPath(ConvertToModelItems(candidatePath));
                candidateReport["targetHierarchyPath"] = BuildActualHierarchyPath(finalNode);
                candidateReport["identityProperties"] = GetIdentityPropertySummaries(finalNode.Item);
                reports.Add(candidateReport);
            }

            return reports;
        }

        private static List<object> GetIdentityPropertySummaries(ModelItem modelItem)
        {
            List<object> summaries = new List<object>();
            if (modelItem == null)
                return summaries;

            try
            {
                foreach (PropertyCategory category in modelItem.PropertyCategories)
                {
                    foreach (DataProperty property in category.Properties)
                    {
                        if (!IsIdentityProperty(property.DisplayName))
                            continue;

                        string value = GetPropertyValueText(property);
                        if (string.IsNullOrWhiteSpace(value))
                            continue;

                        Dictionary<string, object> summary = new Dictionary<string, object>();
                        summary["categoryDisplayName"] = category.DisplayName ?? "";
                        summary["propertyDisplayName"] = property.DisplayName ?? "";
                        summary["value"] = value;
                        summaries.Add(summary);
                    }
                }
            }
            catch
            {
                // Candidate paths remain useful even if properties cannot be read.
            }

            return summaries;
        }

        private static string BuildActualHierarchyPath(TargetNodeInfo node)
        {
            List<string> reversedNames = new List<string>();
            TargetNodeInfo current = node;
            while (current != null)
            {
                if (!string.IsNullOrEmpty(current.DisplayName))
                    reversedNames.Add(current.DisplayName);
                current = current.Parent;
            }

            StringBuilder builder = new StringBuilder();
            for (int index = reversedNames.Count - 1; index >= 0; index--)
            {
                if (builder.Length > 0)
                    builder.Append(" / ");
                builder.Append(reversedNames[index]);
            }

            return builder.ToString();
        }

        private static List<ModelItem> GetChildrenSafely(
            ModelItem parent,
            ref int nodesInspected)
        {
            List<ModelItem> children = new List<ModelItem>();
            if (parent == null)
                return children;

            try
            {
                foreach (ModelItem child in parent.Children)
                {
                    CountInspectedNode(ref nodesInspected);
                    children.Add(child);
                }
            }
            catch (SearchLimitException)
            {
                throw;
            }

            return children;
        }

        private static void CountInspectedNode(ref int nodesInspected)
        {
            nodesInspected++;
            if (nodesInspected > MaxNodesInspected)
                throw new SearchLimitException();
        }

        private static string ReadSourceJson(out string selectedFilePath)
        {
            selectedFilePath = null;
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "Выберите JSON, экспортированный из исходного NWF";
                dialog.Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*";
                dialog.Multiselect = false;
                dialog.InitialDirectory = Environment.GetFolderPath(
                    Environment.SpecialFolder.Desktop);

                if (dialog.ShowDialog() != DialogResult.OK)
                    return null;

                selectedFilePath = dialog.FileName;
                return File.ReadAllText(dialog.FileName, Encoding.UTF8);
            }
        }


        private static string ReadNativePolicyJson(out string selectedFilePath)
        {
            selectedFilePath = null;
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "Выберите UniversalNativeRebuildProfile_v1.json или Native Rebuild policy";
                dialog.Filter = "Native rebuild policy (*.json)|*.json|All files (*.*)|*.*";
                dialog.Multiselect = false;
                dialog.InitialDirectory = Environment.GetFolderPath(
                    Environment.SpecialFolder.Desktop);

                if (dialog.ShowDialog() != DialogResult.OK)
                    return null;

                selectedFilePath = dialog.FileName;
                return File.ReadAllText(dialog.FileName, Encoding.UTF8);
            }
        }

        private static List<object> ExpandNativeRuleBasedSets(
            List<ExpectedSet> expectedSets,
            Dictionary<string, List<TargetNodeInfo>> indexedRootsByName,
            string targetDocumentTitle,
            ref int nodesInspected,
            out bool searchLimitReached)
        {
            const int maxRuleScanNodesPerRoot = 150000;
            List<object> reports = new List<object>();
            searchLimitReached = false;

            if (expectedSets == null)
                return reports;

            foreach (ExpectedSet expectedSet in expectedSets)
            {
                if (expectedSet == null ||
                    !string.Equals(expectedSet.NativeMode, "ruleBased", StringComparison.OrdinalIgnoreCase))
                    continue;

                Dictionary<string, object> sectionConfig = expectedSet.NativeRuleConfig;
                Dictionary<string, object> sectionReport = new Dictionary<string, object>();
                sectionReport["sectionCode"] = expectedSet.NativeSectionCode;
                sectionReport["selectionSetName"] = expectedSet.Name;
                sectionReport["sourceModelFileName"] = expectedSet.NativeSourceModelFileName;

                List<TargetNodeInfo> mappedRoots = null;
                if (indexedRootsByName == null ||
                    !indexedRootsByName.TryGetValue(expectedSet.NativeSourceModelFileName, out mappedRoots) ||
                    mappedRoots == null || mappedRoots.Count != 1 ||
                    mappedRoots[0] == null || mappedRoots[0].Item == null)
                {
                    expectedSet.NativeRuleExpansionStatus = "BLOCKED_ROOT_NOT_UNIQUE";
                    sectionReport["status"] = expectedSet.NativeRuleExpansionStatus;
                    sectionReport["reason"] = "A unique target linked-model root is required before target-tree rules can be expanded.";
                    sectionReport["expectedRootCount"] = mappedRoots == null ? 0 : mappedRoots.Count;
                    reports.Add(sectionReport);
                    // Keep the placeholder: the root-mapping report independently blocks transfer.
                    continue;
                }

                List<string> categoryAllowList = ReadNativeRuleStringList(sectionConfig, "categoryAllowList");
                List<string> categoryExcludeList = ReadNativeRuleStringList(sectionConfig, "categoryExcludeList");
                List<string> requiredCategoryNames = ReadNativeRuleStringList(sectionConfig, "requiredCategoryNames");
                List<object> rawTypeRules = AsObjectList(GetDictionaryValue(sectionConfig, "includeTypesUnderPaths"));
                List<object> rawPathRules = AsObjectList(GetDictionaryValue(sectionConfig, "includeExplicitPaths"));

                HashSet<string> excludedCategoryNames = new HashSet<string>(categoryExcludeList, StringComparer.OrdinalIgnoreCase);
                HashSet<string> expandPathKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                List<Dictionary<string, object>> normalizedTypeRules = new List<Dictionary<string, object>>();
                List<Dictionary<string, object>> normalizedPathRules = new List<Dictionary<string, object>>();
                bool invalidRule = false;
                string invalidRuleMessage = "";

                foreach (object rawRule in rawTypeRules)
                {
                    Dictionary<string, object> rule = AsDictionary(rawRule);
                    if (rule == null)
                    {
                        invalidRule = true;
                        invalidRuleMessage = "includeTypesUnderPaths contains a non-object rule.";
                        break;
                    }

                    List<string> path = ReadStringPath(rule, "path");
                    if (path.Count < 1)
                    {
                        invalidRule = true;
                        invalidRuleMessage = "Each includeTypesUnderPaths rule needs a non-empty path.";
                        break;
                    }

                    rule["__normalizedPath"] = path;
                    normalizedTypeRules.Add(rule);
                    for (int count = 1; count <= path.Count; count++)
                        expandPathKeys.Add(NativePolicyPathKey(path.GetRange(0, count)));
                }

                if (!invalidRule)
                {
                    foreach (object rawRule in rawPathRules)
                    {
                        Dictionary<string, object> rule = AsDictionary(rawRule);
                        if (rule == null)
                        {
                            invalidRule = true;
                            invalidRuleMessage = "includeExplicitPaths contains a non-object rule.";
                            break;
                        }

                        List<string> path = ReadStringPath(rule, "path");
                        string nodeType = NativePolicyNodeTypeToRussian(GetDictionaryString(rule, "nodeType"));
                        if (path.Count < 1 ||
                            (nodeType != "Семейство" && nodeType != "Тип" && nodeType != "Категория"))
                        {
                            invalidRule = true;
                            invalidRuleMessage = "Each includeExplicitPaths rule needs a path and nodeType Category, Family, or Type.";
                            break;
                        }

                        rule["__normalizedPath"] = path;
                        rule["__normalizedNodeType"] = nodeType;
                        normalizedPathRules.Add(rule);
                        // The terminal path node does not need to be expanded; all prefixes before it do.
                        for (int count = 1; count < path.Count; count++)
                            expandPathKeys.Add(NativePolicyPathKey(path.GetRange(0, count)));
                    }
                }

                if (invalidRule)
                {
                    expectedSet.NativeRuleExpansionStatus = "BLOCKED_INVALID_RULE";
                    sectionReport["status"] = expectedSet.NativeRuleExpansionStatus;
                    sectionReport["reason"] = invalidRuleMessage;
                    reports.Add(sectionReport);
                    // Create a missing synthetic path so the regular preflight cannot treat this as ready.
                    expectedSet.Items.Clear();
                    expectedSet.Items.Add(CreateNativePolicyExpectedItem(
                        targetDocumentTitle,
                        expectedSet.NativeSourceModelFileName,
                        new List<string> { "__INVALID_NATIVE_RULE_CONFIG__" },
                        "Тип"));
                    continue;
                }

                Dictionary<string, List<NativeObservedNode>> observedByPath;
                HashSet<string> observedCategoryNames;
                int scannedNodes;
                bool truncated;
                try
                {
                    ScanNativePolicyTree(
                        mappedRoots[0].Item,
                        expandPathKeys,
                        maxRuleScanNodesPerRoot,
                        ref nodesInspected,
                        out observedByPath,
                        out observedCategoryNames,
                        out scannedNodes,
                        out truncated);
                }
                catch (SearchLimitException)
                {
                    searchLimitReached = true;
                    expectedSet.NativeRuleExpansionStatus = "SEARCH_LIMIT_REACHED";
                    sectionReport["status"] = expectedSet.NativeRuleExpansionStatus;
                    sectionReport["reason"] = "Global node-inspection safety limit reached during rule expansion.";
                    reports.Add(sectionReport);
                    break;
                }

                List<ExpectedItem> expandedItems = new List<ExpectedItem>();
                HashSet<string> addedExpectedPathKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                List<string> categoriesIncluded = new List<string>();
                List<string> categoriesNotFound = new List<string>();
                List<string> requiredCategoriesNotFound = new List<string>();
                List<object> typeRuleReports = new List<object>();
                List<object> explicitPathReports = new List<object>();

                // Category allow-list is deliberate: categories absent on a particular stage are
                // reported, while newly observed/unlisted categories are never auto-included.
                foreach (string categoryName in categoryAllowList)
                {
                    if (excludedCategoryNames.Contains(categoryName))
                        continue;

                    bool found = observedCategoryNames.Contains(categoryName);
                    if (found)
                    {
                        List<string> path = new List<string> { categoryName };
                        if (AddNativeExpectedPath(
                            expandedItems,
                            addedExpectedPathKeys,
                            targetDocumentTitle,
                            expectedSet.NativeSourceModelFileName,
                            path,
                            "Категория"))
                        {
                            categoriesIncluded.Add(categoryName);
                        }
                    }
                    else
                    {
                        categoriesNotFound.Add(categoryName);
                    }
                }

                // Required category signatures protect the OV1/OV2 swap and the critical VK rule.
                // If such a category is absent, add an intentionally-unresolvable sentinel path so
                // ordinary preflight reports a MISSING node and blocks any transfer.
                foreach (string requiredCategory in requiredCategoryNames)
                {
                    if (observedCategoryNames.Contains(requiredCategory))
                        continue;

                    requiredCategoriesNotFound.Add(requiredCategory);
                    AddNativeExpectedPath(
                        expandedItems,
                        addedExpectedPathKeys,
                        targetDocumentTitle,
                        expectedSet.NativeSourceModelFileName,
                        new List<string> { requiredCategory },
                        "Категория");
                }

                foreach (Dictionary<string, object> rule in normalizedTypeRules)
                {
                    List<string> parentPath = (List<string>)rule["__normalizedPath"];
                    List<string> excludedTypes = ReadNativeRuleStringList(rule, "excludeNames");
                    HashSet<string> excludedTypeSet = new HashSet<string>(excludedTypes, StringComparer.OrdinalIgnoreCase);
                    HashSet<string> includedTypeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    List<string> excludedTypeNamesFound = new List<string>();
                    string ruleName = GetDictionaryString(rule, "name");

                    foreach (KeyValuePair<string, List<NativeObservedNode>> observedPair in observedByPath)
                    {
                        foreach (NativeObservedNode observed in observedPair.Value)
                        {
                            if (observed == null || observed.Path == null ||
                                observed.Path.Count != parentPath.Count + 1 ||
                                !NativePathStartsWith(observed.Path, parentPath) ||
                                !IsNativePolicyTerminalType(observed.ElementType, "Тип"))
                                continue;

                            string typeName = observed.Path[observed.Path.Count - 1];
                            if (excludedTypeSet.Contains(typeName))
                            {
                                if (!ContainsIgnoreCase(excludedTypeNamesFound, typeName))
                                    excludedTypeNamesFound.Add(typeName);
                                continue;
                            }

                            if (includedTypeNames.Add(typeName))
                            {
                                AddNativeExpectedPath(
                                    expandedItems,
                                    addedExpectedPathKeys,
                                    targetDocumentTitle,
                                    expectedSet.NativeSourceModelFileName,
                                    observed.Path,
                                    "Тип");
                            }
                        }
                    }

                    Dictionary<string, object> typeReport = new Dictionary<string, object>();
                    typeReport["name"] = ruleName;
                    typeReport["parentPath"] = parentPath;
                    typeReport["includedUniqueTypeCount"] = includedTypeNames.Count;
                    typeReport["includedTypeNames"] = new List<string>(includedTypeNames);
                    typeReport["excludedTypeNamesFound"] = excludedTypeNamesFound;
                    typeReport["required"] = GetDictionaryBoolean(rule, "required", false);
                    if (includedTypeNames.Count == 0 && GetDictionaryBoolean(rule, "required", false))
                    {
                        typeReport["status"] = "REQUIRED_TYPE_RULE_NO_MATCH";
                        List<string> sentinelPath = new List<string>(parentPath);
                        sentinelPath.Add("__REQUIRED_NATIVE_TYPE_RULE_NOT_FOUND__");
                        AddNativeExpectedPath(
                            expandedItems,
                            addedExpectedPathKeys,
                            targetDocumentTitle,
                            expectedSet.NativeSourceModelFileName,
                            sentinelPath,
                            "Тип");
                    }
                    else
                    {
                        typeReport["status"] = includedTypeNames.Count == 0 ? "OPTIONAL_TYPE_RULE_NO_MATCH" : "MATCHED";
                    }
                    typeRuleReports.Add(typeReport);
                }

                foreach (Dictionary<string, object> rule in normalizedPathRules)
                {
                    List<string> expectedPath = (List<string>)rule["__normalizedPath"];
                    string expectedType = (string)rule["__normalizedNodeType"];
                    bool required = GetDictionaryBoolean(rule, "required", false);
                    bool found = false;
                    foreach (KeyValuePair<string, List<NativeObservedNode>> observedPair in observedByPath)
                    {
                        foreach (NativeObservedNode observed in observedPair.Value)
                        {
                            if (observed != null && NativePathEquals(observed.Path, expectedPath) &&
                                IsNativePolicyTerminalType(observed.ElementType, expectedType))
                            {
                                found = true;
                                AddNativeExpectedPath(
                                    expandedItems,
                                    addedExpectedPathKeys,
                                    targetDocumentTitle,
                                    expectedSet.NativeSourceModelFileName,
                                    expectedPath,
                                    expectedType);
                                break;
                            }
                        }
                        if (found) break;
                    }

                    Dictionary<string, object> pathReport = new Dictionary<string, object>();
                    pathReport["path"] = expectedPath;
                    pathReport["nodeType"] = expectedType;
                    pathReport["required"] = required;
                    pathReport["status"] = found ? "MATCHED" : (required ? "REQUIRED_PATH_NOT_FOUND" : "OPTIONAL_PATH_NOT_FOUND");
                    explicitPathReports.Add(pathReport);
                    if (!found && required)
                    {
                        AddNativeExpectedPath(
                            expandedItems,
                            addedExpectedPathKeys,
                            targetDocumentTitle,
                            expectedSet.NativeSourceModelFileName,
                            expectedPath,
                            expectedType);
                    }
                }

                expectedSet.Items.Clear();
                expectedSet.Items.AddRange(expandedItems);
                expectedSet.NativeRuleExpansionItemCount = expandedItems.Count;
                expectedSet.NativeRuleExpansionStatus = truncated ? "SEARCH_TRUNCATED" :
                    (requiredCategoriesNotFound.Count > 0 ? "REQUIRED_CATEGORY_NOT_FOUND" : "EXPANDED");

                sectionReport["status"] = expectedSet.NativeRuleExpansionStatus;
                sectionReport["nodesScanned"] = scannedNodes;
                sectionReport["treeScanTruncated"] = truncated;
                sectionReport["includedCategoryNames"] = categoriesIncluded;
                sectionReport["configuredCategoryNamesNotFound"] = categoriesNotFound;
                sectionReport["requiredCategoryNamesNotFound"] = requiredCategoriesNotFound;
                sectionReport["dynamicTypeRules"] = typeRuleReports;
                sectionReport["explicitPathRules"] = explicitPathReports;
                sectionReport["expandedExpectedItemCount"] = expandedItems.Count;
                sectionReport["observedCategoryNameCount"] = observedCategoryNames.Count;
                sectionReport["autoIncludedUnlistedCategories"] = false;
                reports.Add(sectionReport);

                if (truncated)
                {
                    searchLimitReached = true;
                    break;
                }
            }

            return reports;
        }

        private static void ScanNativePolicyTree(
            ModelItem linkedRoot,
            HashSet<string> expandPathKeys,
            int maxNodesToScan,
            ref int nodesInspected,
            out Dictionary<string, List<NativeObservedNode>> observedByPath,
            out HashSet<string> observedCategoryNames,
            out int scannedNodes,
            out bool truncated)
        {
            observedByPath = new Dictionary<string, List<NativeObservedNode>>(StringComparer.OrdinalIgnoreCase);
            observedCategoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            scannedNodes = 0;
            truncated = false;
            if (linkedRoot == null)
                return;

            Queue<NativeRuleWalkNode> queue = new Queue<NativeRuleWalkNode>();
            NativeRuleWalkNode root = new NativeRuleWalkNode();
            root.Item = linkedRoot;
            root.Path = new List<string>();
            root.Depth = 0;
            queue.Enqueue(root);

            while (queue.Count > 0)
            {
                NativeRuleWalkNode current = queue.Dequeue();
                if (current == null || current.Item == null)
                    continue;

                if (current.Depth >= 12)
                    continue;

                IEnumerable<ModelItem> children;
                try
                {
                    children = current.Item.Children;
                }
                catch
                {
                    continue;
                }

                foreach (ModelItem child in children)
                {
                    if (child == null)
                        continue;
                    scannedNodes++;
                    if (scannedNodes > maxNodesToScan)
                    {
                        truncated = true;
                        return;
                    }
                    CountInspectedNode(ref nodesInspected);

                    string className = SafeModelItemClassName(child);
                    bool isLayer = string.Equals(className, "LcRevitLayer", StringComparison.OrdinalIgnoreCase);
                    List<string> nodePath = isLayer
                        ? new List<string>(current.Path)
                        : AppendNativePathSegment(current.Path, SafeDisplayName(child));
                    if (!isLayer && nodePath.Count > 0)
                    {
                        string elementType = GetModelItemPropertyValue(child, "Элемент", "Тип");
                        NativeObservedNode observed = new NativeObservedNode();
                        observed.Item = child;
                        observed.Path = nodePath;
                        observed.DisplayName = SafeDisplayName(child);
                        observed.ElementType = elementType ?? "";
                        string pathKey = NativePolicyPathKey(nodePath);
                        List<NativeObservedNode> samePathNodes;
                        if (!observedByPath.TryGetValue(pathKey, out samePathNodes))
                        {
                            samePathNodes = new List<NativeObservedNode>();
                            observedByPath.Add(pathKey, samePathNodes);
                        }
                        samePathNodes.Add(observed);

                        if (nodePath.Count == 1 && IsCategoryType(observed.ElementType))
                            observedCategoryNames.Add(observed.DisplayName);
                    }

                    bool shouldExpand = isLayer;
                    if (!isLayer)
                    {
                        string elementType = GetModelItemPropertyValue(child, "Элемент", "Тип");
                        shouldExpand = IsCategoryType(elementType) || expandPathKeys.Contains(NativePolicyPathKey(nodePath));
                    }

                    if (shouldExpand)
                    {
                        NativeRuleWalkNode next = new NativeRuleWalkNode();
                        next.Item = child;
                        next.Path = nodePath;
                        next.Depth = current.Depth + 1;
                        queue.Enqueue(next);
                    }
                }
            }
        }

        private static bool AddNativeExpectedPath(
            List<ExpectedItem> items,
            HashSet<string> addedPathKeys,
            string documentRootName,
            string sourceModelFileName,
            List<string> path,
            string terminalNodeType)
        {
            if (items == null || addedPathKeys == null || path == null || path.Count == 0)
                return false;

            string key = NativePolicyPathKey(path) + "|" + (terminalNodeType ?? "");
            if (!addedPathKeys.Add(key))
                return false;

            ExpectedItem item = CreateNativePolicyExpectedItem(
                documentRootName,
                sourceModelFileName,
                new List<string>(path),
                terminalNodeType);
            item.DisplayName = path[path.Count - 1];
            items.Add(item);
            return true;
        }

        private static bool ContainsIgnoreCase(List<string> values, string value)
        {
            if (values == null) return false;
            foreach (string item in values)
                if (string.Equals(item, value, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private static List<string> ReadNativeRuleStringList(Dictionary<string, object> dictionary, string key)
        {
            List<string> result = new List<string>();
            foreach (object raw in AsObjectList(GetDictionaryValue(dictionary, key)))
            {
                string value = raw == null ? "" : Convert.ToString(raw, CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(value) &&
                    !ContainsIgnoreCase(result, value))
                    result.Add(value);
            }
            return result;
        }

        private static List<string> ReadStringPath(Dictionary<string, object> dictionary, string key)
        {
            List<string> result = new List<string>();
            foreach (object raw in AsObjectList(GetDictionaryValue(dictionary, key)))
            {
                string segment = raw == null ? "" : Convert.ToString(raw, CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(segment))
                    return new List<string>();
                result.Add(segment);
            }
            return result;
        }

        private static bool GetDictionaryBoolean(Dictionary<string, object> dictionary, string key, bool defaultValue)
        {
            object value = GetDictionaryValue(dictionary, key);
            if (value == null) return defaultValue;
            if (value is bool) return (bool)value;
            bool parsed;
            return bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out parsed) ? parsed : defaultValue;
        }

        private static string NativePolicyPathKey(List<string> path)
        {
            return string.Join("\u001f", path ?? new List<string>());
        }

        private static List<string> AppendNativePathSegment(List<string> currentPath, string segment)
        {
            List<string> result = currentPath == null ? new List<string>() : new List<string>(currentPath);
            if (!string.IsNullOrWhiteSpace(segment))
                result.Add(segment);
            return result;
        }

        private static bool NativePathEquals(List<string> left, List<string> right)
        {
            if (left == null || right == null || left.Count != right.Count)
                return false;
            for (int index = 0; index < left.Count; index++)
                if (!string.Equals(left[index], right[index], StringComparison.OrdinalIgnoreCase))
                    return false;
            return true;
        }

        private static bool NativePathStartsWith(List<string> path, List<string> prefix)
        {
            if (path == null || prefix == null || path.Count < prefix.Count)
                return false;
            for (int index = 0; index < prefix.Count; index++)
                if (!string.Equals(path[index], prefix[index], StringComparison.OrdinalIgnoreCase))
                    return false;
            return true;
        }

        private static bool IsNativePolicyTerminalType(string actualType, string expectedType)
        {
            string actual = NativePolicyNodeTypeToRussian(actualType);
            if (string.IsNullOrWhiteSpace(actual)) actual = actualType ?? "";
            string expected = NativePolicyNodeTypeToRussian(expectedType);
            if (string.IsNullOrWhiteSpace(expected)) expected = expectedType ?? "";
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }

        // Universal mode expands section profiles against linked RVT roots in the active NWF.
        // Project prefix is derived from the NWF filename; the stage token and root count are not fixed.
        private static List<ExpectedSet> ReadUniversalNativeRebuildPolicyExpectedSets(
            Dictionary<string, object> profileDocument,
            Document document,
            string targetDocumentTitle)
        {
            Dictionary<string, object> targetConfig =
                AsDictionary(GetDictionaryValue(profileDocument, "target"));
            if (targetConfig == null ||
                !GetDictionaryBoolean(targetConfig, "autoDiscoverLinkedRoots", false))
            {
                throw new InvalidDataException(
                    "Выбранный профиль не включает autoDiscoverLinkedRoots=true.");
            }

            List<object> rawProfiles = AsObjectList(GetDictionaryValue(profileDocument, "sectionProfiles"));
            if (rawProfiles.Count == 0)
                throw new InvalidDataException("В универсальном профиле отсутствует массив sectionProfiles.");

            Dictionary<string, Dictionary<string, object>> profilesByCode =
                new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            foreach (object rawProfile in rawProfiles)
            {
                Dictionary<string, object> profile = AsDictionary(rawProfile);
                if (profile == null)
                    throw new InvalidDataException("Каждый элемент sectionProfiles должен быть JSON-объектом.");

                string profileCode = GetDictionaryString(profile, "sectionCode");
                if (string.IsNullOrWhiteSpace(profileCode))
                    throw new InvalidDataException("У профиля раздела отсутствует sectionCode.");
                if (profilesByCode.ContainsKey(profileCode))
                    throw new InvalidDataException("В sectionProfiles повторяется sectionCode: " + profileCode);

                profilesByCode.Add(profileCode, profile);
            }

            Dictionary<string, object> configuredNames =
                AsDictionary(GetDictionaryValue(profileDocument, "setNamesBySection"));
            if (configuredNames == null)
                configuredNames = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            string projectPrefix = ExtractProjectPrefixBeforeRdMarker(targetDocumentTitle);
            if (string.IsNullOrWhiteSpace(projectPrefix))
            {
                throw new InvalidDataException(
                    "Не удалось определить префикс проекта в имени NWF. Ожидается имя с маркером _РД_, " +
                    "например Project_РД_ПРК_СБ.nwf. Файл не изменён.");
            }

            int nodesInspected = 0;
            List<ModelItem> targetRoots = FindAllLinkedModelRoots(document, ref nodesInspected);
            List<object> dynamicSections = new List<object>();
            Dictionary<string, object> dynamicSetNames =
                new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> detectedSectionCodes =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> matchingRootNames = new List<string>();
            List<string> unsupportedRootNames = new List<string>();

            foreach (ModelItem root in targetRoots)
            {
                string displayName = SafeDisplayName(root);
                string fileName = ExtractModelFileName(displayName);
                if (string.IsNullOrWhiteSpace(fileName))
                    continue;

                string rootProjectPrefix = ExtractProjectPrefixBeforeRdMarker(fileName);
                if (!string.Equals(projectPrefix, rootProjectPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                string sectionCode = ExtractUniversalSectionCode(
                    fileName, profilesByCode.Keys);
                if (string.IsNullOrWhiteSpace(sectionCode))
                {
                    unsupportedRootNames.Add(displayName + " [не удалось определить код раздела]");
                    continue;
                }

                Dictionary<string, object> sectionProfile;
                if (!profilesByCode.TryGetValue(sectionCode, out sectionProfile))
                {
                    unsupportedRootNames.Add(displayName + " [профиль не настроен: " + sectionCode + "]");
                    continue;
                }

                if (!detectedSectionCodes.Add(sectionCode))
                {
                    throw new InvalidDataException(
                        "Найдено несколько связанных RVT одного раздела '" + sectionCode +
                        "'. Автоматически объединять их в один Selection Set небезопасно. Корень: " + displayName);
                }

                Dictionary<string, object> runtimeSection =
                    new Dictionary<string, object>(sectionProfile, StringComparer.OrdinalIgnoreCase);
                string selectionSetName = GetDictionaryString(configuredNames, sectionCode);
                if (string.IsNullOrWhiteSpace(selectionSetName))
                    selectionSetName = GetDictionaryString(sectionProfile, "selectionSetName");
                if (string.IsNullOrWhiteSpace(selectionSetName))
                    selectionSetName = TransliterateSectionCodeToSetName(sectionCode);
                if (string.IsNullOrWhiteSpace(selectionSetName))
                {
                    throw new InvalidDataException(
                        "Не удалось получить имя Selection Set для раздела '" + sectionCode + "'.");
                }

                runtimeSection["sectionCode"] = sectionCode;
                runtimeSection["selectionSetName"] = selectionSetName;
                runtimeSection["sourceModelFileName"] = fileName;
                runtimeSection["expectedTargetModelFileName"] = fileName;
                dynamicSections.Add(runtimeSection);
                dynamicSetNames[sectionCode] = selectionSetName;
                matchingRootNames.Add(sectionCode + " -> " + fileName + " -> " + selectionSetName);
            }

            if (dynamicSections.Count == 0)
            {
                throw new InvalidDataException(
                    "В текущем NWF не найдено ни одного корня проекта '" + projectPrefix +
                    "', для которого настроен профиль. Проверь _РД_ в имени RVT и список sectionProfiles.");
            }

            Dictionary<string, object> runtimePolicy =
                new Dictionary<string, object>(profileDocument, StringComparer.OrdinalIgnoreCase);
            runtimePolicy["sections"] = dynamicSections;
            runtimePolicy["setNamesBySection"] = dynamicSetNames;
            runtimePolicy["universalNativeRebuild"] = true;
            runtimePolicy["universalProjectPrefix"] = projectPrefix;
            runtimePolicy["universalDetectedRoots"] = matchingRootNames;
            runtimePolicy["universalUnsupportedRootsAtExpansion"] = unsupportedRootNames;

            Dictionary<string, object> runtimeTarget =
                new Dictionary<string, object>(targetConfig, StringComparer.OrdinalIgnoreCase);
            runtimeTarget["autoDiscoverLinkedRoots"] = true;
            runtimeTarget["expectedLinkedRootCount"] = 0;
            runtimePolicy["target"] = runtimeTarget;

            // The main report and unmapped-root preflight use these runtime flags.
            profileDocument["universalNativeRebuild"] = true;
            profileDocument["universalProjectPrefix"] = projectPrefix;
            profileDocument["universalDetectedRoots"] = matchingRootNames;
            profileDocument["universalUnsupportedRootsAtExpansion"] = unsupportedRootNames;

            return ReadNativeRebuildPolicyExpectedSets(runtimePolicy, targetDocumentTitle);
        }

        private static string ExtractProjectPrefixBeforeRdMarker(string fileOrDisplayName)
        {
            if (string.IsNullOrWhiteSpace(fileOrDisplayName))
                return "";

            string fileName = Path.GetFileName(fileOrDisplayName.Trim());
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = fileOrDisplayName.Trim();

            int markerIndex = fileName.IndexOf("_РД_", StringComparison.OrdinalIgnoreCase);
            if (markerIndex <= 0)
                return "";

            return fileName.Substring(0, markerIndex).Trim();
        }

        private static string ExtractUniversalSectionCode(
            string modelFileName,
            IEnumerable<string> configuredSectionCodes)
        {
            string fileName = ExtractModelFileName(modelFileName);
            if (string.IsNullOrWhiteSpace(fileName))
                return "";

            int markerIndex = fileName.IndexOf("_РД_", StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
                return "";

            // The discipline code is a complete underscore-delimited token somewhere
            // after _РД_. This supports both ..._РД_ОВ1_ПРК_... and ..._РД_ПРК_ОВ1_...
            // without confusing OV1 with OV2 or matching a substring inside another token.
            string remainder = fileName.Substring(markerIndex + "_РД_".Length);
            string[] tokens = remainder.Split(new char[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
            string bestMatch = "";
            if (configuredSectionCodes != null)
            {
                foreach (string candidate in configuredSectionCodes)
                {
                    if (string.IsNullOrWhiteSpace(candidate))
                        continue;

                    foreach (string token in tokens)
                    {
                        if (string.Equals(token, candidate, StringComparison.OrdinalIgnoreCase) &&
                            candidate.Length > bestMatch.Length)
                        {
                            bestMatch = candidate;
                        }
                    }
                }
            }

            return bestMatch;
        }

        private static List<ExpectedSet> ReadNativeRebuildPolicyExpectedSets(
            Dictionary<string, object> policyDocument,
            string targetDocumentTitle)
        {
            if (!string.Equals(
                GetDictionaryString(policyDocument, "documentType"),
                "NavisCoordinator.NativeRebuildPolicyDraft",
                StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Файл не является политикой NavisCoordinator.NativeRebuildPolicyDraft.");
            }

            int formatVersion;
            if (!int.TryParse(
                Convert.ToString(GetDictionaryValue(policyDocument, "formatVersion"), CultureInfo.InvariantCulture),
                out formatVersion) || formatVersion != 1)
            {
                throw new InvalidDataException("Для native rebuild ожидается formatVersion = 1.");
            }

            Dictionary<string, object> targetConfig =
                AsDictionary(GetDictionaryValue(policyDocument, "target"));
            if (targetConfig == null)
                throw new InvalidDataException("В политике отсутствует объект 'target'.");

            bool autoDiscoverLinkedRoots = GetDictionaryBoolean(
                targetConfig, "autoDiscoverLinkedRoots", false);

            if (!autoDiscoverLinkedRoots)
            {
                string expectedProjectPrefix = GetDictionaryString(targetConfig, "projectPrefix");
                string expectedStageToken = GetDictionaryString(targetConfig, "stageToken");
                string actualProjectPrefix;
                string actualStageToken;
                if (!TryGetProjectAndStageToken(
                        targetDocumentTitle,
                        out actualProjectPrefix,
                        out actualStageToken))
                {
                    throw new InvalidDataException(
                        "Не удалось определить проект и этап из имени текущего NWF: " + targetDocumentTitle);
                }

                if (!string.Equals(expectedProjectPrefix, actualProjectPrefix, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(expectedStageToken, actualStageToken, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "Политика предназначена для " + expectedProjectPrefix + " / " + expectedStageToken +
                        ", а открыт документ " + actualProjectPrefix + " / " + actualStageToken +
                        ". Выберите корректную копию NWF и соответствующую политику.");
                }
            }

            List<object> rawSections = AsObjectList(GetDictionaryValue(policyDocument, "sections"));
            if (rawSections.Count == 0)
                throw new InvalidDataException("В политике не найден массив 'sections'.");

            Dictionary<string, object> setNamesBySection =
                AsDictionary(GetDictionaryValue(policyDocument, "setNamesBySection"));
            if (setNamesBySection == null)
                throw new InvalidDataException("В политике отсутствует закреплённая карта setNamesBySection.");

            List<ExpectedSet> result = new List<ExpectedSet>();
            HashSet<string> seenSetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> seenSectionCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string syntheticDocumentRootName = string.IsNullOrWhiteSpace(targetDocumentTitle)
                ? "NATIVE_REBUILD_POLICY"
                : targetDocumentTitle;

            foreach (object rawSection in rawSections)
            {
                Dictionary<string, object> section = AsDictionary(rawSection);
                if (section == null)
                    throw new InvalidDataException("Каждый раздел политики должен быть JSON-объектом.");

                string sectionCode = GetDictionaryString(section, "sectionCode");
                string selectionSetName = GetDictionaryString(section, "selectionSetName");
                string mode = GetDictionaryString(section, "mode");
                string sourceModelFileName = ExtractModelFileName(
                    GetDictionaryString(section, "sourceModelFileName"));
                string expectedTargetModelFileName = ExtractModelFileName(
                    GetDictionaryString(section, "expectedTargetModelFileName"));

                if (string.IsNullOrWhiteSpace(sectionCode) ||
                    string.IsNullOrWhiteSpace(selectionSetName) ||
                    string.IsNullOrWhiteSpace(sourceModelFileName) ||
                    string.IsNullOrWhiteSpace(expectedTargetModelFileName))
                {
                    throw new InvalidDataException(
                        "У раздела политики обязательны sectionCode, selectionSetName, sourceModelFileName и expectedTargetModelFileName.");
                }

                if (!seenSetNames.Add(selectionSetName))
                    throw new InvalidDataException("Повторяется имя Selection Set в политике: " + selectionSetName);
                if (!seenSectionCodes.Add(sectionCode))
                    throw new InvalidDataException("Повторяется sectionCode в политике: " + sectionCode);

                string canonicalName = GetDictionaryString(setNamesBySection, sectionCode);
                if (string.IsNullOrWhiteSpace(canonicalName) ||
                    !string.Equals(canonicalName, selectionSetName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "Имя Selection Set для раздела '" + sectionCode + "' расходится с setNamesBySection. " +
                        "Ожидалось '" + canonicalName + "', в sections задано '" + selectionSetName + "'.");
                }

                if (!string.Equals(mode, "wholeLink", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(mode, "explicitNodes", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(mode, "ruleBased", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "Неизвестный mode='" + mode + "' для раздела " + sectionCode + ". " +
                        "Допустимы wholeLink, explicitNodes, ruleBased.");
                }

                ExpectedSet expectedSet = new ExpectedSet();
                expectedSet.Name = selectionSetName;
                expectedSet.IsNativePolicySet = true;
                expectedSet.NativeSectionCode = sectionCode;
                expectedSet.NativeMode = mode;
                expectedSet.NativeSourceModelFileName = sourceModelFileName;
                expectedSet.NativeExpectedTargetModelFileName = expectedTargetModelFileName;

                if (string.Equals(mode, "wholeLink", StringComparison.OrdinalIgnoreCase))
                {
                    ExpectedItem rootItem = CreateNativePolicyExpectedItem(
                        syntheticDocumentRootName,
                        sourceModelFileName,
                        new List<string>(),
                        "Ссылка");
                    expectedSet.Items.Add(rootItem);
                }
                else if (string.Equals(mode, "ruleBased", StringComparison.OrdinalIgnoreCase))
                {
                    // Keep a temporary root-only item so root mapping can be performed
                    // before runtime expansion discovers the actual category/type paths.
                    expectedSet.NativeRuleConfig = section;
                    ExpectedItem rootPlaceholder = CreateNativePolicyExpectedItem(
                        syntheticDocumentRootName,
                        sourceModelFileName,
                        new List<string>(),
                        "Ссылка");
                    expectedSet.Items.Add(rootPlaceholder);

                    List<object> categoryAllowList = AsObjectList(GetDictionaryValue(section, "categoryAllowList"));
                    List<object> typeRules = AsObjectList(GetDictionaryValue(section, "includeTypesUnderPaths"));
                    List<object> pathRules = AsObjectList(GetDictionaryValue(section, "includeExplicitPaths"));
                    if (categoryAllowList.Count == 0 && typeRules.Count == 0 && pathRules.Count == 0)
                    {
                        throw new InvalidDataException(
                            "Раздел " + sectionCode + " с mode=ruleBased должен содержать categoryAllowList, " +
                            "includeTypesUnderPaths или includeExplicitPaths.");
                    }
                }
                else
                {
                    List<object> rawItems = AsObjectList(GetDictionaryValue(section, "items"));
                    if (rawItems.Count == 0)
                    {
                        throw new InvalidDataException(
                            "Раздел " + sectionCode + " имеет mode=explicitNodes, но не содержит items.");
                    }

                    foreach (object rawItem in rawItems)
                    {
                        Dictionary<string, object> item = AsDictionary(rawItem);
                        if (item == null)
                            throw new InvalidDataException("Элемент политики должен быть JSON-объектом.");

                        string itemName = GetDictionaryString(item, "name");
                        string nodeType = GetDictionaryString(item, "nodeType");
                        List<object> rawPath = AsObjectList(GetDictionaryValue(item, "path"));
                        List<string> path = new List<string>();
                        foreach (object rawSegment in rawPath)
                        {
                            string segment = rawSegment == null ? "" : Convert.ToString(rawSegment, CultureInfo.InvariantCulture);
                            if (string.IsNullOrWhiteSpace(segment))
                                throw new InvalidDataException(
                                    "Пустой сегмент path у элемента '" + itemName + "' в разделе " + sectionCode + ".");
                            path.Add(segment);
                        }

                        if (string.IsNullOrWhiteSpace(itemName) || path.Count == 0 ||
                            !string.Equals(path[path.Count - 1], itemName, StringComparison.Ordinal))
                        {
                            throw new InvalidDataException(
                                "У элемента '" + itemName + "' path должен быть непустым и заканчиваться самим элементом.");
                        }

                        string terminalType = NativePolicyNodeTypeToRussian(nodeType);
                        if (terminalType != "Категория" && terminalType != "Семейство" && terminalType != "Тип")
                        {
                            throw new InvalidDataException(
                                "nodeType должен быть Category, Family или Type; получено '" + nodeType + "'.");
                        }

                        if (terminalType == "Category" || terminalType == "Категория")
                        {
                            if (path.Count != 1)
                                throw new InvalidDataException("Для Category ожидается путь из одного сегмента: " + itemName);
                        }
                        if (terminalType == "Семейство" && path.Count < 2)
                            throw new InvalidDataException("Для Family нужен путь через категорию: " + itemName);
                        if (terminalType == "Тип" && path.Count < 3)
                            throw new InvalidDataException("Для Type нужен путь категория / семейство / тип: " + itemName);

                        ExpectedItem expectedItem = CreateNativePolicyExpectedItem(
                            syntheticDocumentRootName,
                            sourceModelFileName,
                            path,
                            terminalType);
                        expectedItem.DisplayName = itemName;
                        expectedSet.Items.Add(expectedItem);
                    }
                }

                if (expectedSet.Items.Count == 0)
                    throw new InvalidDataException("У раздела " + sectionCode + " нет ни одного ожидаемого элемента.");

                result.Add(expectedSet);
            }

            int expectedRootCount = ToInt32(GetDictionaryValue(targetConfig, "expectedLinkedRootCount"));
            if (expectedRootCount > 0 && result.Count != expectedRootCount)
            {
                throw new InvalidDataException(
                    "Политика содержит разделов: " + result.Count + "; ожидалось: " + expectedRootCount + ".");
            }

            return result;
        }

        private static ExpectedItem CreateNativePolicyExpectedItem(
            string documentRootName,
            string sourceModelFileName,
            List<string> path,
            string terminalNodeType)
        {
            ExpectedItem item = new ExpectedItem();
            item.DisplayName = path != null && path.Count > 0
                ? path[path.Count - 1]
                : sourceModelFileName;
            item.HierarchyNodes = new List<Dictionary<string, object>>();
            item.HierarchyNodes.Add(CreateSyntheticHierarchyNode(documentRootName, "Файл"));
            item.HierarchyNodes.Add(CreateSyntheticHierarchyNode(sourceModelFileName, "Ссылка"));

            if (path != null)
            {
                for (int index = 0; index < path.Count; index++)
                {
                    string nodeType;
                    if (index == path.Count - 1)
                    {
                        nodeType = terminalNodeType;
                    }
                    else if (string.Equals(terminalNodeType, "Тип", StringComparison.Ordinal) && index == path.Count - 2)
                    {
                        nodeType = "Семейство";
                    }
                    else
                    {
                        nodeType = "Категория";
                    }

                    item.HierarchyNodes.Add(CreateSyntheticHierarchyNode(path[index], nodeType));
                }
            }

            StringBuilder hierarchyPath = new StringBuilder(documentRootName);
            hierarchyPath.Append(" / ").Append(sourceModelFileName);
            if (path != null)
            {
                foreach (string segment in path)
                    hierarchyPath.Append(" / ").Append(segment);
            }
            item.SourceHierarchyPath = hierarchyPath.ToString();
            return item;
        }

        private static Dictionary<string, object> CreateSyntheticHierarchyNode(
            string displayName,
            string elementType)
        {
            Dictionary<string, object> property = new Dictionary<string, object>();
            property["categoryName"] = "LcOaNode";
            property["categoryDisplayName"] = "Элемент";
            property["propertyName"] = "LcOaSceneBaseClassUserName";
            property["propertyDisplayName"] = "Тип";
            property["value"] = elementType ?? "";

            Dictionary<string, object> node = new Dictionary<string, object>();
            node["displayName"] = displayName ?? "";
            node["properties"] = new List<object> { property };
            return node;
        }

        private static string NativePolicyNodeTypeToRussian(string nodeType)
        {
            if (string.Equals(nodeType, "Category", StringComparison.OrdinalIgnoreCase)) return "Категория";
            if (string.Equals(nodeType, "Family", StringComparison.OrdinalIgnoreCase)) return "Семейство";
            if (string.Equals(nodeType, "Type", StringComparison.OrdinalIgnoreCase)) return "Тип";
            if (string.Equals(nodeType, "WholeLink", StringComparison.OrdinalIgnoreCase)) return "Ссылка";
            return "";
        }

        private static string ValidateNativePolicyRootOverrides(
            Dictionary<string, object> policyDocument,
            Dictionary<string, string> actualOverrides,
            bool rootOverridesFileExists)
        {
            Dictionary<string, object> rootMapping =
                AsDictionary(GetDictionaryValue(policyDocument, "rootMapping"));
            List<object> expectedOverrideItems = rootMapping == null
                ? new List<object>()
                : AsObjectList(GetDictionaryValue(rootMapping, "expectedOverrides"));

            if (expectedOverrideItems.Count > 0 && !rootOverridesFileExists)
            {
                return "Политика требует rootOverrides.json с правилами переназначения ОВ1↔ОВ2, " +
                       "но рядом с файлом политики он не найден.";
            }

            Dictionary<string, string> policyOverrides =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (object raw in expectedOverrideItems)
            {
                Dictionary<string, object> entry = AsDictionary(raw);
                if (entry == null)
                    return "Некорректная запись expectedOverrides в политике.";
                string source = ExtractModelFileName(GetDictionaryString(entry, "sourceModelFileName"));
                string target = ExtractModelFileName(GetDictionaryString(entry, "targetModelFileName"));
                if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target) ||
                    policyOverrides.ContainsKey(source))
                    return "В expectedOverrides политики обнаружена пустая или повторная запись.";
                policyOverrides.Add(source, target);
            }

            if (policyOverrides.Count != (actualOverrides == null ? 0 : actualOverrides.Count))
            {
                return "Количество правил rootOverrides.json не совпадает с политикой. Ожидалось: " +
                    policyOverrides.Count + "; фактически: " + (actualOverrides == null ? 0 : actualOverrides.Count) + ".";
            }

            foreach (KeyValuePair<string, string> item in policyOverrides)
            {
                string actualTarget;
                if (actualOverrides == null || !actualOverrides.TryGetValue(item.Key, out actualTarget) ||
                    !string.Equals(actualTarget, item.Value, StringComparison.OrdinalIgnoreCase))
                {
                    return "Правило rootOverrides для '" + item.Key + "' не совпадает с политикой. " +
                        "Ожидалось: '" + item.Value + "'. Проверьте файл rootOverrides.json.";
                }
            }

            List<object> rawSections = AsObjectList(GetDictionaryValue(policyDocument, "sections"));
            foreach (object rawSection in rawSections)
            {
                Dictionary<string, object> section = AsDictionary(rawSection);
                if (section == null) continue;
                string source = ExtractModelFileName(GetDictionaryString(section, "sourceModelFileName"));
                string expectedTarget = ExtractModelFileName(GetDictionaryString(section, "expectedTargetModelFileName"));
                string actualTarget = source;
                if (actualOverrides != null && actualOverrides.ContainsKey(source))
                    actualTarget = actualOverrides[source];

                if (!string.Equals(expectedTarget, actualTarget, StringComparison.OrdinalIgnoreCase))
                {
                    return "Ожидаемый целевой корень раздела '" + GetDictionaryString(section, "sectionCode") +
                        "' расходится с фактическим rootOverrides. Ожидалось '" + expectedTarget +
                        "', по фактической конфигурации получается '" + actualTarget + "'.";
                }
            }

            return "";
        }

        private static List<ExpectedSet> ReadExpectedSets(
            Dictionary<string, object> sourceDocument)
        {
            List<ExpectedSet> result = new List<ExpectedSet>();
            List<object> rawSets = AsObjectList(GetDictionaryValue(sourceDocument, "selectionSets"));

            foreach (object rawSet in rawSets)
            {
                Dictionary<string, object> setDictionary = AsDictionary(rawSet);
                if (setDictionary == null)
                    continue;

                ExpectedSet expectedSet = new ExpectedSet();
                expectedSet.Name = GetDictionaryString(setDictionary, "name");

                List<object> rawItems = AsObjectList(GetDictionaryValue(setDictionary, "items"));
                foreach (object rawItem in rawItems)
                {
                    Dictionary<string, object> itemDictionary = AsDictionary(rawItem);
                    if (itemDictionary == null)
                        continue;

                    ExpectedItem expectedItem = new ExpectedItem();
                    expectedItem.DisplayName = GetDictionaryString(itemDictionary, "displayName");
                    expectedItem.SourceHierarchyPath = GetDictionaryString(itemDictionary, "hierarchyPath");
                    expectedItem.HierarchyNodes = new List<Dictionary<string, object>>();

                    List<object> rawHierarchy = AsObjectList(GetDictionaryValue(itemDictionary, "hierarchy"));
                    foreach (object rawNode in rawHierarchy)
                    {
                        Dictionary<string, object> nodeDictionary = AsDictionary(rawNode);
                        if (nodeDictionary != null)
                            expectedItem.HierarchyNodes.Add(nodeDictionary);
                    }

                    if (string.IsNullOrEmpty(expectedItem.DisplayName) && expectedItem.HierarchyNodes.Count > 0)
                    {
                        expectedItem.DisplayName = GetDictionaryString(
                            expectedItem.HierarchyNodes[expectedItem.HierarchyNodes.Count - 1],
                            "displayName");
                    }

                    expectedSet.Items.Add(expectedItem);
                }

                result.Add(expectedSet);
            }

            return result;
        }

        private static List<string> GetSourcePathSegments(
            List<Dictionary<string, object>> hierarchyNodes)
        {
            List<string> segments = new List<string>();
            if (hierarchyNodes == null || hierarchyNodes.Count < 2)
                return segments;

            // Ignore the top-level NWC file name; target container names may differ.
            for (int index = 1; index < hierarchyNodes.Count; index++)
            {
                string name = GetDictionaryString(hierarchyNodes[index], "displayName");
                if (string.IsNullOrEmpty(name))
                    return new List<string>();

                segments.Add(name);
            }

            return segments;
        }

        private static List<string> CompareIdentityProperties(
            ExpectedItem expectedItem,
            List<ModelItem> targetPathNodes)
        {
            List<string> warnings = new List<string>();
            if (expectedItem.HierarchyNodes == null || targetPathNodes == null)
                return warnings;

            // Skip the source NWC root and the linked-RVT root: the latter
            // has already been mapped by RVT file name/rootOverrides and its
            // IfcGUID may legitimately change after migration. Candidate paths
            // contain the target linked-RVT root at index 0; intermediate
            // LcRevitLayer level nodes are excluded from this logical path.
            // Therefore source node 2 aligns with target node 1.
            int sourceIndex = 2;
            int targetIndex = 1;
            while (sourceIndex < expectedItem.HierarchyNodes.Count && targetIndex < targetPathNodes.Count)
            {
                Dictionary<string, object> sourceNode = expectedItem.HierarchyNodes[sourceIndex];
                ModelItem targetNode = targetPathNodes[targetIndex];
                string nodeName = GetDictionaryString(sourceNode, "displayName");
                List<object> sourceProperties = AsObjectList(
                    GetDictionaryValue(sourceNode, "properties"));

                foreach (object rawProperty in sourceProperties)
                {
                    Dictionary<string, object> propertyDictionary = AsDictionary(rawProperty);
                    if (propertyDictionary == null)
                        continue;

                    string propertyDisplayName = GetDictionaryString(propertyDictionary, "propertyDisplayName");
                    if (!IsIdentityProperty(propertyDisplayName))
                        continue;

                    string sourceValue = NormalizeDisplayValue(GetDictionaryString(propertyDictionary, "value"));
                    if (string.IsNullOrWhiteSpace(sourceValue))
                        continue;

                    string categoryDisplayName = GetDictionaryString(propertyDictionary, "categoryDisplayName");
                    string targetValue = GetModelItemPropertyValue(
                        targetNode,
                        categoryDisplayName,
                        propertyDisplayName);

                    if (string.IsNullOrWhiteSpace(targetValue))
                    {
                        warnings.Add(
                            nodeName + ": в целевом узле не найдено свойство '" +
                            propertyDisplayName + "', присутствовавшее в исходном экспорте.");
                    }
                    else if (!string.Equals(sourceValue, targetValue, StringComparison.Ordinal))
                    {
                        warnings.Add(
                            nodeName + ": свойство '" + propertyDisplayName +
                            "' изменилось (исходное='" + sourceValue +
                            "', целевое='" + targetValue + "').");
                    }
                }

                sourceIndex++;
                targetIndex++;
            }

            return warnings;
        }

        private static bool IsIdentityProperty(string propertyDisplayName)
        {
            return string.Equals(propertyDisplayName, "IfcGUID", StringComparison.OrdinalIgnoreCase)
                || string.Equals(propertyDisplayName, "Тип IfcGUID", StringComparison.OrdinalIgnoreCase)
                || string.Equals(propertyDisplayName, "Id", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetModelItemPropertyValue(
            ModelItem modelItem,
            string categoryDisplayName,
            string propertyDisplayName)
        {
            try
            {
                if (modelItem == null)
                    return "";

                foreach (PropertyCategory category in modelItem.PropertyCategories)
                {
                    if (!string.Equals(
                        category.DisplayName ?? "",
                        categoryDisplayName ?? "",
                        StringComparison.Ordinal))
                    {
                        continue;
                    }

                    foreach (DataProperty property in category.Properties)
                    {
                        if (string.Equals(
                            property.DisplayName ?? "",
                            propertyDisplayName ?? "",
                            StringComparison.Ordinal))
                        {
                            return GetPropertyValueText(property);
                        }
                    }
                }
            }
            catch
            {
                // A missing/unavailable property is reported as an identity warning.
            }

            return "";
        }

        private static string GetPropertyValueText(DataProperty property)
        {
            try
            {
                return NormalizeDisplayValue(property.Value.ToDisplayString());
            }
            catch
            {
                try
                {
                    return NormalizeDisplayValue(property.Value.ToString());
                }
                catch
                {
                    return "";
                }
            }
        }

        private static string NormalizeDisplayValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";

            const string displayStringPrefix = "DisplayString:";
            string normalized = value.Trim();
            if (normalized.StartsWith(displayStringPrefix, StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring(displayStringPrefix.Length).Trim();

            return normalized;
        }

        private static string BuildDisplayPath(List<ModelItem> pathNodes)
        {
            StringBuilder builder = new StringBuilder();
            for (int index = 0; index < pathNodes.Count; index++)
            {
                if (index > 0)
                    builder.Append(" / ");

                builder.Append(SafeDisplayName(pathNodes[index]));
            }

            return builder.ToString();
        }

        private static string SafeDisplayName(ModelItem modelItem)
        {
            if (modelItem == null)
                return "";

            try
            {
                return modelItem.DisplayName ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static int CountExpectedItems(List<ExpectedSet> sets)
        {
            int count = 0;
            foreach (ExpectedSet set in sets)
                count += set.Items.Count;
            return count;
        }

        private static Dictionary<string, object> AsDictionary(object value)
        {
            return value as Dictionary<string, object>;
        }

        private static List<object> AsObjectList(object value)
        {
            List<object> result = new List<object>();
            IEnumerable enumerable = value as IEnumerable;

            if (enumerable == null || value is string || value is IDictionary)
                return result;

            foreach (object item in enumerable)
                result.Add(item);

            return result;
        }

        private static object GetDictionaryValue(Dictionary<string, object> dictionary, string key)
        {
            if (dictionary == null || string.IsNullOrEmpty(key))
                return null;

            object value;
            return dictionary.TryGetValue(key, out value) ? value : null;
        }

        private static string GetDictionaryString(Dictionary<string, object> dictionary, string key)
        {
            object value = GetDictionaryValue(dictionary, key);
            return value == null ? "" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }

        private static void ShowSummary(
            string reportPath,
            string sourceTitle,
            string targetTitle,
            int setCount,
            int expectedCount,
            int matchedCount,
            int oneToManyCategoryCount,
            int oneToManyIdentityCount,
            int oneToManyStructuralCount,
            int oneToManyNativePolicyCount,
            int identityDisambiguatedCount,
            int missingCount,
            int ambiguousCount,
            int searchLimitCount,
            int identityWarningCount,
            int nodesInspected,
            int targetNodesIndexed,
            int targetPathSearchVisits,
            bool searchLimitReached,
            bool transferRequested,
            bool transferApplied,
            string transferStatus,
            int createdSelectionSetCount,
            int replacedSelectionSetCount,
            int createdTargetModelItemCount,
            string transferError)
        {
            StringBuilder message = new StringBuilder();
            message.AppendLine(transferRequested
                ? "Обработка переноса завершена."
                : "Проверка завершена. Документ не изменялся.");
            message.AppendLine();
            message.AppendLine("Исходный документ в JSON: " + sourceTitle);
            message.AppendLine("Текущий целевой документ: " + targetTitle);
            message.AppendLine();
            message.AppendLine("Наборов проверено: " + setCount);
            message.AppendLine("Исходных узлов: " + expectedCount);
            message.AppendLine("Однозначно сопоставлено: " + matchedCount);
            message.AppendLine("Категорий сопоставлено один-ко-многим: " + oneToManyCategoryCount);
            message.AppendLine("Узлов сопоставлено один-ко-многим по ID/GUID: " + oneToManyIdentityCount);
            message.AppendLine("Контейнеров сопоставлено один-ко-многим по структуре: " + oneToManyStructuralCount);
            message.AppendLine("Узлов сопоставлено по политике нативного построения: " + oneToManyNativePolicyCount);
            message.AppendLine("Неоднозначность снята выбором одного пути по ID/GUID: " + identityDisambiguatedCount);
            message.AppendLine("Не найдено: " + missingCount);
            message.AppendLine("Неоднозначных соответствий: " + ambiguousCount);
            message.AppendLine("Поиск не завершён для узлов: " + searchLimitCount);
            message.AppendLine("Предупреждений по ID/GUID: " + identityWarningCount);
            message.AppendLine("Узлов просмотрено при поиске и индексации: " + nodesInspected);
            message.AppendLine("Узлов целевого дерева проиндексировано: " + targetNodesIndexed);
            message.AppendLine("Проверок узлов при сопоставлении путей: " + targetPathSearchVisits);
            message.AppendLine("Лимит безопасного поиска достигнут: " + (searchLimitReached ? "да" : "нет"));
            message.AppendLine();
            message.AppendLine("Отчёт сохранён:");
            message.AppendLine(reportPath);
            message.AppendLine();
            if (!transferRequested)
            {
                message.AppendLine("Selection Sets не создавались и не изменялись.");
            }
            else if (transferApplied)
            {
                message.AppendLine("Создано новых Selection Sets: " + createdSelectionSetCount);
                message.AppendLine("Заменено существующих Selection Sets: " + replacedSelectionSetCount);
                message.AppendLine("Всего целевых ModelItem во всех наборах: " + createdTargetModelItemCount);
                message.AppendLine("Статус переноса: " + transferStatus);
                message.AppendLine("Документ изменён в памяти. Сохраните рабочую копию NWF вручную, если результат проверен.");
            }
            else
            {
                message.AppendLine("Перенос не завершён успешно.");
                message.AppendLine("Статус переноса: " + transferStatus);
                if (string.Equals(transferStatus, "FAILED_ROLLBACK_INCOMPLETE", StringComparison.Ordinal))
                {
                    message.AppendLine("ВНИМАНИЕ: часть добавленных наборов могла остаться в документе.");
                    message.AppendLine("Не сохраняйте NWF. Проверьте дерево Sets и при необходимости закройте копию без сохранения.");
                    message.AppendLine("Наборов, всё ещё обнаруженных в документе по результату проверки: " + createdSelectionSetCount);
                }
                else
                {
                    message.AppendLine("Selection Sets не были перенесены.");
                }
                if (!string.IsNullOrWhiteSpace(transferError))
                {
                    message.AppendLine("Причина:");
                    message.AppendLine(transferError);
                }
            }

            if (sourceTitle.IndexOf("_П3_", StringComparison.OrdinalIgnoreCase) >= 0 &&
                targetTitle.IndexOf("_П3_", StringComparison.OrdinalIgnoreCase) < 0)
            {
                message.AppendLine();
                message.AppendLine("ВНИМАНИЕ: JSON относится к П3, а название текущего NWF не содержит '_П3_'. Проверьте, что открыт целевой документ того же проекта/этажа.");
            }

            MessageBox.Show(
                message.ToString(),
                "Navis Coordinator — результат проверки",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        private static string PrettyPrintJson(string json)
        {
            StringBuilder builder = new StringBuilder();
            int indentation = 0;
            bool insideString = false;
            bool escaped = false;

            for (int index = 0; index < json.Length; index++)
            {
                char current = json[index];

                if (insideString)
                {
                    builder.Append(current);
                    if (escaped)
                        escaped = false;
                    else if (current == '\\')
                        escaped = true;
                    else if (current == '"')
                        insideString = false;
                    continue;
                }

                if (char.IsWhiteSpace(current))
                    continue;

                if (current == '"')
                {
                    insideString = true;
                    builder.Append(current);
                    continue;
                }

                if (current == '{' || current == '[')
                {
                    builder.Append(current);
                    int nextNonWhitespace = FindNextNonWhitespace(json, index + 1);
                    char matchingClose = current == '{' ? '}' : ']';
                    if (nextNonWhitespace < json.Length && json[nextNonWhitespace] == matchingClose)
                    {
                        builder.Append(matchingClose);
                        index = nextNonWhitespace;
                    }
                    else
                    {
                        indentation++;
                        builder.AppendLine();
                        AppendIndentation(builder, indentation);
                    }
                    continue;
                }

                if (current == '}' || current == ']')
                {
                    builder.AppendLine();
                    indentation--;
                    if (indentation < 0)
                        indentation = 0;
                    AppendIndentation(builder, indentation);
                    builder.Append(current);
                    continue;
                }

                if (current == ',')
                {
                    builder.Append(current);
                    builder.AppendLine();
                    AppendIndentation(builder, indentation);
                    continue;
                }

                if (current == ':')
                {
                    builder.Append(": ");
                    continue;
                }

                builder.Append(current);
            }

            return builder.ToString();
        }

        private static int FindNextNonWhitespace(string text, int startIndex)
        {
            for (int index = startIndex; index < text.Length; index++)
            {
                if (!char.IsWhiteSpace(text[index]))
                    return index;
            }

            return text.Length;
        }

        private static void AppendIndentation(StringBuilder builder, int indentation)
        {
            for (int index = 0; index < indentation; index++)
                builder.Append("  ");
        }

        private sealed class NativeRuleWalkNode
        {
            public ModelItem Item;
            public List<string> Path = new List<string>();
            public int Depth;
        }

        private sealed class NativeObservedNode
        {
            public ModelItem Item;
            public List<string> Path = new List<string>();
            public string DisplayName = "";
            public string ElementType = "";
        }

        private sealed class TargetNodeInfo
        {
            public ModelItem Item;
            public string DisplayName = "";
            public TargetNodeInfo Parent;
            public int Depth;
            public string ElementType = "";
            public bool ElementTypeLoaded;
            public bool ChildrenLoaded;
            public List<TargetNodeInfo> Children = new List<TargetNodeInfo>();
        }

        private sealed class ExpectedSet
        {
            public string Name = "";
            public bool GeneratedFromUnmappedTargetRoot;
            public string GeneratedTargetModelFileName = "";
            public bool IsNativePolicySet;
            public string NativeSectionCode = "";
            public string NativeMode = "";
            public string NativeSourceModelFileName = "";
            public string NativeExpectedTargetModelFileName = "";
            public Dictionary<string, object> NativeRuleConfig;
            public string NativeRuleExpansionStatus = "NOT_APPLICABLE";
            public int NativeRuleExpansionItemCount;
            public List<ExpectedItem> Items = new List<ExpectedItem>();
        }

        private sealed class ExpectedItem
        {
            public string DisplayName = "";
            public string SourceHierarchyPath = "";
            public List<Dictionary<string, object>> HierarchyNodes =
                new List<Dictionary<string, object>>();
        }

        private sealed class IdentityValue
        {
            public string CategoryDisplayName = "";
            public string PropertyDisplayName = "";
            public string Value = "";
        }

        private sealed class SearchLimitException : Exception
        {
        }
    }
}