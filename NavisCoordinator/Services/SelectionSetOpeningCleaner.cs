using Autodesk.Navisworks.Api;
using System;
using System.Collections.Generic;
using System.Text;

namespace NavisCoordinator.Services
{
    /// <summary>
    /// Removes opening-related nodes only from the "Обобщенные модели" category
    /// inside the existing KG Selection Set. The analysis phase is read-only.
    /// </summary>
    public static class SelectionSetOpeningCleaner
    {
        private const string TargetSelectionSetName = "KG";
        private const int MaxNodesToInspect = 500000;
        private const int MaxHierarchyDepth = 80;

        private static readonly string[] GenericModelsCategoryNames =
        {
            "Обобщенные модели",
            "Обобщённые модели",
            "Generic Models"
        };

        private const string OpeningNameStem = "отверст";

        public sealed class CleanupPlan
        {
            internal Document Document;
            internal GroupItem Parent;
            internal int Index;
            internal SelectionSet OriginalSet;
            internal ModelItemCollection CleanedItems;

            public string SelectionSetName { get; internal set; }
            public int OriginalItemCount { get; internal set; }
            public int CleanedItemCount { get { return CleanedItems == null ? 0 : CleanedItems.Count; } }
            public int NodesInspected { get; internal set; }
            public int GenericModelsCategoryCount { get; internal set; }
            public List<string> RemovedNodePaths { get; internal set; }
            public bool HasChanges { get { return RemovedNodePaths != null && RemovedNodePaths.Count > 0; } }
            public bool CanApply
            {
                get
                {
                    return HasChanges && GenericModelsCategoryCount > 0 &&
                           CleanedItems != null && CleanedItems.Count > 0;
                }
            }
        }

        private sealed class FoundSet
        {
            public SelectionSet Set;
            public GroupItem Parent;
            public int Index;
        }

        private sealed class ScanState
        {
            public int NodesInspected;
            public int GenericModelsCategoryCount;
            public readonly List<string> RemovedNodePaths = new List<string>();
            public readonly HashSet<string> RemovedPathKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> GenericModelsCategoryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class NodeResult
        {
            public bool Changed;
            public readonly List<ModelItem> Items = new List<ModelItem>();
        }

        /// <summary>
        /// Builds a read-only cleanup plan for the unique top-level/foldered KG set.
        /// No Selection Set or Clash Test is changed by this method.
        /// </summary>
        public static CleanupPlan CreatePlan(Document document)
        {
            if (document == null || document.IsClear)
                throw new InvalidOperationException("Нет открытого документа Navisworks.");

            List<FoundSet> matches = new List<FoundSet>();
            FindSelectionSets(
                document.SelectionSets.RootItem.Children,
                document.SelectionSets.RootItem,
                TargetSelectionSetName,
                matches);

            if (matches.Count == 0)
                throw new InvalidOperationException(
                    "В документе не найден Selection Set с именем '" + TargetSelectionSetName + "'.");

            if (matches.Count > 1)
                throw new InvalidOperationException(
                    "Найдено несколько Selection Sets с именем '" + TargetSelectionSetName + "'. " +
                    "Для безопасной очистки имя должно быть уникальным.");

            FoundSet found = matches[0];
            if (found.Set == null || !found.Set.HasExplicitModelItems)
                throw new InvalidOperationException(
                    "Selection Set '" + TargetSelectionSetName + "' не является статическим Selection Set " +
                    "с явным списком ModelItems. Search Set автоматически не изменяется.");

            ModelItemCollection originalItems = found.Set.ExplicitModelItems;
            if (originalItems == null || originalItems.Count == 0)
                throw new InvalidOperationException(
                    "Selection Set '" + TargetSelectionSetName + "' не содержит явных ModelItems.");

            CleanupPlan plan = new CleanupPlan();
            plan.Document = document;
            plan.Parent = found.Parent;
            plan.Index = found.Index;
            plan.OriginalSet = found.Set;
            plan.SelectionSetName = found.Set.DisplayName;
            plan.OriginalItemCount = originalItems.Count;
            plan.RemovedNodePaths = new List<string>();
            plan.CleanedItems = new ModelItemCollection();

            ScanState state = new ScanState();
            foreach (ModelItem selectedItem in originalItems)
            {
                if (selectedItem == null)
                    continue;

                NodeResult result;
                ModelItem genericModelsAncestor;
                if (TryFindGenericModelsAncestor(selectedItem, out genericModelsAncestor))
                {
                    RegisterGenericModelsCategory(state, genericModelsAncestor);
                    result = PruneInsideGenericModels(selectedItem, state, 0);
                }
                else
                {
                    result = FindAndPruneGenericModelsBelow(selectedItem, state, 0);
                }

                foreach (ModelItem keptItem in result.Items)
                {
                    if (keptItem != null && !plan.CleanedItems.Contains(keptItem))
                        plan.CleanedItems.Add(keptItem);
                }
            }

            plan.NodesInspected = state.NodesInspected;
            plan.GenericModelsCategoryCount = state.GenericModelsCategoryCount;
            plan.RemovedNodePaths = state.RemovedNodePaths;

            if (plan.GenericModelsCategoryCount == 0)
            {
                throw new InvalidOperationException(
                    "Внутри Selection Set 'KG' не удалось обнаружить категорию 'Обобщенные модели'. " +
                    "Ничего не изменено. Возможно, набор содержит другие уровни дерева — нужен диагностический дамп KG.");
            }

            return plan;
        }

        /// <summary>
        /// Applies a previously reviewed plan by replacing a copy of the same saved item.
        /// The Selection Set name, folder position and other saved-item metadata are retained.
        /// </summary>
        public static void Apply(CleanupPlan plan)
        {
            if (plan == null || !plan.CanApply)
                throw new InvalidOperationException("План очистки пустой или не содержит безопасных изменений.");

            Document activeDocument = Autodesk.Navisworks.Api.Application.ActiveDocument;
            if (activeDocument == null || !object.ReferenceEquals(activeDocument, plan.Document))
                throw new InvalidOperationException(
                    "Активный документ изменился после Dry Run. Постройте план заново.");

            if (plan.Parent == null || plan.Index < 0 || plan.Index >= plan.Parent.Children.Count)
                throw new InvalidOperationException(
                    "Не удалось подтвердить исходное расположение Selection Set 'KG'. Постройте план заново.");

            SavedItem currentSavedItem = plan.Parent.Children[plan.Index];
            SelectionSet currentSet = currentSavedItem as SelectionSet;
            if (currentSet == null ||
                !string.Equals(currentSet.DisplayName ?? "", plan.SelectionSetName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Selection Set 'KG' изменился после Dry Run. Постройте план заново.");
            }

            if (!currentSet.HasExplicitModelItems ||
                currentSet.ExplicitModelItems.Count != plan.OriginalItemCount)
            {
                throw new InvalidOperationException(
                    "Состав Selection Set 'KG' изменился после Dry Run. Постройте план заново.");
            }

            SelectionSet backup = currentSet.CreateCopy() as SelectionSet;
            SelectionSet replacement = currentSet.CreateCopy() as SelectionSet;
            if (backup == null || replacement == null)
                throw new InvalidOperationException(
                    "Не удалось создать резервную копию Selection Set 'KG'. Ничего не изменено.");

            replacement.ExplicitModelItems.CopyFrom(plan.CleanedItems);

            try
            {
                plan.Document.SelectionSets.ReplaceWithCopy(plan.Parent, plan.Index, replacement);

                SelectionSet appliedSet = plan.Parent.Children[plan.Index] as SelectionSet;
                if (appliedSet == null ||
                    !string.Equals(appliedSet.DisplayName ?? "", plan.SelectionSetName, StringComparison.OrdinalIgnoreCase) ||
                    !appliedSet.HasExplicitModelItems ||
                    appliedSet.ExplicitModelItems.Count != plan.CleanedItemCount)
                {
                    throw new InvalidOperationException(
                        "После замены не удалось подтвердить ожидаемый состав Selection Set 'KG'.");
                }
            }
            catch (Exception applyException)
            {
                string rollbackMessage = "";
                try
                {
                    if (plan.Index < plan.Parent.Children.Count)
                        plan.Document.SelectionSets.ReplaceWithCopy(plan.Parent, plan.Index, backup);
                }
                catch (Exception rollbackException)
                {
                    rollbackMessage = "\nОткат также завершился ошибкой: " + rollbackException.Message;
                }

                throw new InvalidOperationException(
                    "Не удалось применить очистку Selection Set 'KG'. " +
                    "Была выполнена попытка восстановить исходный набор. " +
                    applyException.Message + rollbackMessage,
                    applyException);
            }
        }

        public static string BuildReport(CleanupPlan plan)
        {
            if (plan == null)
                return "План очистки отсутствует.";

            StringBuilder builder = new StringBuilder();
            builder.AppendLine("NavisCoordinator — KG / очистка отверстий");
            builder.AppendLine("Режим: Dry Run, документ не изменён");
            builder.AppendLine("Selection Set: " + plan.SelectionSetName);
            builder.AppendLine("Исходных элементов верхнего уровня: " + plan.OriginalItemCount);
            builder.AppendLine("Обнаружено веток категории 'Обобщенные модели': " + plan.GenericModelsCategoryCount);
            builder.AppendLine("Просмотрено узлов дерева: " + plan.NodesInspected);
            builder.AppendLine("Найдено веток с названием, содержащим 'отверст': " +
                               (plan.RemovedNodePaths == null ? 0 : plan.RemovedNodePaths.Count));
            builder.AppendLine("Элементов верхнего уровня после очистки: " + plan.CleanedItemCount);
            builder.AppendLine();

            if (plan.RemovedNodePaths == null || plan.RemovedNodePaths.Count == 0)
            {
                builder.AppendLine("Совпадений не найдено. Selection Set не требуется изменять.");
            }
            else
            {
                builder.AppendLine("Исключаемые узлы:");
                for (int index = 0; index < plan.RemovedNodePaths.Count; index++)
                {
                    builder.Append(index + 1);
                    builder.Append(". ");
                    builder.AppendLine(plan.RemovedNodePaths[index]);
                }
            }

            builder.AppendLine();
            builder.AppendLine("Другие Selection Sets и Clash Tests этим инструментом не изменяются.");
            builder.AppendLine("NWF автоматически не сохраняется.");
            return builder.ToString();
        }

        private static void FindSelectionSets(
            SavedItemCollection items,
            GroupItem parent,
            string targetName,
            List<FoundSet> found)
        {
            if (items == null)
                return;

            for (int index = 0; index < items.Count; index++)
            {
                SavedItem savedItem = items[index];
                if (savedItem == null)
                    continue;

                SelectionSet selectionSet = savedItem as SelectionSet;
                if (selectionSet != null &&
                    string.Equals(selectionSet.DisplayName ?? "", targetName, StringComparison.OrdinalIgnoreCase))
                {
                    FoundSet match = new FoundSet();
                    match.Set = selectionSet;
                    match.Parent = parent;
                    match.Index = index;
                    found.Add(match);
                }

                GroupItem group = savedItem as GroupItem;
                if (group != null)
                    FindSelectionSets(group.Children, group, targetName, found);
            }
        }

        private static NodeResult PruneInsideGenericModels(
            ModelItem item,
            ScanState state,
            int depth)
        {
            CountNode(state, item, depth);

            if (ContainsOpeningName(SafeDisplayName(item)))
            {
                string matchPath = BuildModelItemPath(item);
                if (state.RemovedPathKeys.Add(matchPath))
                    state.RemovedNodePaths.Add(matchPath);
                return ChangedResult();
            }

            List<ModelItem> children = GetChildren(item);
            if (children.Count == 0)
                return KeepResult(item);

            List<ModelItem> retainedChildren = new List<ModelItem>();
            bool childChanged = false;

            foreach (ModelItem child in children)
            {
                NodeResult childResult = PruneInsideGenericModels(child, state, depth + 1);
                if (childResult.Changed)
                    childChanged = true;
                retainedChildren.AddRange(childResult.Items);
            }

            if (!childChanged)
                return KeepResult(item);

            NodeResult result = ChangedResult();
            result.Items.AddRange(retainedChildren);
            return result;
        }

        private static NodeResult FindAndPruneGenericModelsBelow(
            ModelItem item,
            ScanState state,
            int depth)
        {
            if (IsGenericModelsCategory(item))
            {
                RegisterGenericModelsCategory(state, item);
                return PruneInsideGenericModels(item, state, depth);
            }

            CountNode(state, item, depth);
            List<ModelItem> children = GetChildren(item);
            if (children.Count == 0)
                return KeepResult(item);

            List<ModelItem> retainedChildren = new List<ModelItem>();
            bool childChanged = false;

            foreach (ModelItem child in children)
            {
                NodeResult childResult = FindAndPruneGenericModelsBelow(child, state, depth + 1);
                if (childResult.Changed)
                    childChanged = true;
                retainedChildren.AddRange(childResult.Items);
            }

            if (!childChanged)
                return KeepResult(item);

            NodeResult result = ChangedResult();
            result.Items.AddRange(retainedChildren);
            return result;
        }

        private static bool TryFindGenericModelsAncestor(ModelItem item, out ModelItem categoryNode)
        {
            categoryNode = null;
            ModelItem current = item;
            int depth = 0;
            while (current != null && depth < MaxHierarchyDepth)
            {
                if (IsGenericModelsCategory(current))
                {
                    categoryNode = current;
                    return true;
                }
                current = current.Parent;
                depth++;
            }
            return false;
        }

        private static void RegisterGenericModelsCategory(ScanState state, ModelItem categoryNode)
        {
            string path = BuildModelItemPath(categoryNode);
            if (state.GenericModelsCategoryPaths.Add(path))
                state.GenericModelsCategoryCount++;
        }

        private static bool IsGenericModelsCategory(ModelItem item)
        {
            if (item == null)
                return false;

            string name = SafeDisplayName(item);
            bool nameMatches = false;
            foreach (string categoryName in GenericModelsCategoryNames)
            {
                if (string.Equals(name, categoryName, StringComparison.OrdinalIgnoreCase))
                {
                    nameMatches = true;
                    break;
                }
            }

            if (!nameMatches)
                return false;

            // In this project's Revit NWC hierarchy, category nodes expose
            // Элемент -> Тип = Категория. If the property is unavailable, the
            // exact category display name is still used as a conservative fallback.
            string elementType = GetElementType(item);
            return string.IsNullOrWhiteSpace(elementType) ||
                   string.Equals(elementType, "Категория", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(elementType, "Category", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsOpeningName(string displayName)
        {
            return !string.IsNullOrWhiteSpace(displayName) &&
                   displayName.IndexOf(OpeningNameStem, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string GetElementType(ModelItem item)
        {
            try
            {
                foreach (PropertyCategory category in item.PropertyCategories)
                {
                    if (!string.Equals(category.DisplayName ?? "", "Элемент", StringComparison.OrdinalIgnoreCase))
                        continue;

                    foreach (DataProperty property in category.Properties)
                    {
                        if (string.Equals(property.DisplayName ?? "", "Тип", StringComparison.OrdinalIgnoreCase))
                            return NormalizePropertyText(GetPropertyText(property));
                    }
                }
            }
            catch
            {
                // Category matching falls back to the exact Generic Models display name.
            }
            return "";
        }

        private static string GetPropertyText(DataProperty property)
        {
            try
            {
                return property.Value == null ? "" : property.Value.ToDisplayString();
            }
            catch
            {
                try { return property.Value == null ? "" : property.Value.ToString(); }
                catch { return ""; }
            }
        }

        private static string NormalizePropertyText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";

            const string prefix = "DisplayString:";
            string normalized = value.Trim();
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring(prefix.Length).Trim();
            return normalized;
        }

        private static List<ModelItem> GetChildren(ModelItem item)
        {
            List<ModelItem> children = new List<ModelItem>();
            try
            {
                foreach (ModelItem child in item.Children)
                {
                    if (child != null)
                        children.Add(child);
                }
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    "Не удалось прочитать дочерние элементы узла '" + SafeDisplayName(item) + "'. " +
                    "Очистка остановлена без изменения документа. " + exception.Message,
                    exception);
            }
            return children;
        }

        private static void CountNode(ScanState state, ModelItem item, int depth)
        {
            if (depth > MaxHierarchyDepth)
                throw new InvalidOperationException(
                    "Превышена максимальная глубина дерева Navisworks (" + MaxHierarchyDepth + "). " +
                    "Очистка остановлена без изменения документа.");

            state.NodesInspected++;
            if (state.NodesInspected > MaxNodesToInspect)
                throw new InvalidOperationException(
                    "Сканирование превысило лимит " + MaxNodesToInspect + " узлов. " +
                    "Очистка остановлена без изменения документа.");
        }

        private static string BuildModelItemPath(ModelItem item)
        {
            List<string> parts = new List<string>();
            ModelItem current = item;
            int depth = 0;
            while (current != null && depth < MaxHierarchyDepth)
            {
                string name = SafeDisplayName(current);
                if (!string.IsNullOrWhiteSpace(name))
                    parts.Add(name);
                current = current.Parent;
                depth++;
            }
            parts.Reverse();
            return string.Join(" / ", parts.ToArray());
        }

        private static string SafeDisplayName(ModelItem item)
        {
            try { return item == null ? "" : item.DisplayName ?? ""; }
            catch { return ""; }
        }

        private static NodeResult KeepResult(ModelItem item)
        {
            NodeResult result = new NodeResult();
            result.Items.Add(item);
            return result;
        }

        private static NodeResult ChangedResult()
        {
            return new NodeResult { Changed = true };
        }
    }
}
