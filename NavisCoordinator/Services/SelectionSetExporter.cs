using Autodesk.Navisworks.Api;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace NavisCoordinator.Services
{
    /// <summary>
    /// POC exporter for diagnosing and later restoring Navisworks Selection Sets.
    /// The current version only reads the document and writes a JSON file.
    /// It does not modify the NWF or any Selection Set.
    /// </summary>
    public static class SelectionSetExporter
    {
        public static void ExportSelectionSets()
        {
            try
            {
                Document document = Autodesk.Navisworks.Api.Application.ActiveDocument;

                if (document == null || document.IsClear)
                {
                    MessageBox.Show(
                        "Сначала откройте NWF/NWD с Selection Sets.",
                        "Navis Coordinator",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return;
                }

                List<object> selectionSets = new List<object>();
                ExportStatistics statistics = new ExportStatistics();

                foreach (SavedItem child in document.SelectionSets.RootItem.Children)
                {
                    ExportSavedItem(
                        child,
                        "",
                        selectionSets,
                        statistics);
                }

                Dictionary<string, object> exportDocument = new Dictionary<string, object>();
                exportDocument["formatVersion"] = 2;
                exportDocument["documentTitle"] = document.Title ?? "";
                exportDocument["exportedAt"] = DateTime.Now.ToString(
                    "yyyy-MM-dd HH:mm:ss",
                    CultureInfo.InvariantCulture);
                exportDocument["selectionSetCount"] = selectionSets.Count;
                exportDocument["selectedModelItemCount"] = statistics.TotalSelectedItems;
                exportDocument["itemsWithNonZeroInstanceGuid"] = statistics.ItemsWithNonZeroInstanceGuid;
                exportDocument["itemsWithZeroInstanceGuid"] = statistics.ItemsWithZeroInstanceGuid;
                exportDocument["selectionSets"] = selectionSets;

                JavaScriptSerializer serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = int.MaxValue;
                serializer.RecursionLimit = 100;
                string json = serializer.Serialize(exportDocument);

                using (SaveFileDialog dialog = new SaveFileDialog())
                {
                    dialog.Title = "Сохранить диагностику Selection Sets";
                    dialog.Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*";
                    dialog.FileName = "NavisCoordinator_SelectionSets_v2_" +
                                      DateTime.Now.ToString("yyyyMMdd_HHmmss") +
                                      ".json";
                    dialog.InitialDirectory = Environment.GetFolderPath(
                        Environment.SpecialFolder.Desktop);

                    if (dialog.ShowDialog() != DialogResult.OK)
                        return;

                    File.WriteAllText(
                        dialog.FileName,
                        json,
                        new UTF8Encoding(false));

                    ShowExportSummary(
                        dialog.FileName,
                        selectionSets.Count,
                        statistics);
                }
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    "Ошибка при экспорте Selection Sets:\n\n" + exception,
                    "Navis Coordinator",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private static void ExportSavedItem(
            SavedItem item,
            string parentPath,
            List<object> selectionSets,
            ExportStatistics statistics)
        {
            if (item == null)
                return;

            string displayName = SafeSavedItemDisplayName(item);
            string currentPath = string.IsNullOrEmpty(parentPath)
                ? displayName
                : parentPath + " / " + displayName;

            SelectionSet selectionSet = item as SelectionSet;

            if (selectionSet != null)
            {
                Dictionary<string, object> exportedSet = ExportSelectionSet(
                    selectionSet,
                    currentPath,
                    statistics);

                selectionSets.Add(exportedSet);
                return;
            }

            if (!item.IsGroup)
                return;

            GroupItem groupItem = item as GroupItem;
            if (groupItem == null)
                return;

            foreach (SavedItem child in groupItem.Children)
            {
                ExportSavedItem(
                    child,
                    currentPath,
                    selectionSets,
                    statistics);
            }
        }

        private static Dictionary<string, object> ExportSelectionSet(
            SelectionSet selectionSet,
            string path,
            ExportStatistics statistics)
        {
            ModelItemCollection selectedItems = selectionSet.GetSelectedItems();
            List<object> exportedItems = new List<object>();
            int setItemsWithNonZeroGuid = 0;
            int setItemsWithZeroGuid = 0;

            foreach (ModelItem modelItem in selectedItems)
            {
                Dictionary<string, object> exportedItem = ExportModelItem(modelItem);
                exportedItems.Add(exportedItem);
                statistics.TotalSelectedItems++;

                bool hasNonZeroGuid = Convert.ToBoolean(
                    exportedItem["hasNonZeroInstanceGuid"],
                    CultureInfo.InvariantCulture);

                if (hasNonZeroGuid)
                {
                    statistics.ItemsWithNonZeroInstanceGuid++;
                    setItemsWithNonZeroGuid++;
                }
                else
                {
                    statistics.ItemsWithZeroInstanceGuid++;
                    setItemsWithZeroGuid++;
                }
            }

            Dictionary<string, object> result = new Dictionary<string, object>();
            result["name"] = selectionSet.DisplayName ?? "";
            result["path"] = path;
            result["hasExplicitModelItems"] = selectionSet.HasExplicitModelItems;
            result["itemCount"] = selectedItems.Count;
            result["itemsWithNonZeroInstanceGuid"] = setItemsWithNonZeroGuid;
            result["itemsWithZeroInstanceGuid"] = setItemsWithZeroGuid;
            result["items"] = exportedItems;

            return result;
        }

        private static Dictionary<string, object> ExportModelItem(ModelItem modelItem)
        {
            Guid instanceGuid = GetInstanceGuid(modelItem);
            List<object> hierarchy = new List<object>();
            List<string> hierarchyNames = new List<string>();

            foreach (ModelItem hierarchyItem in GetHierarchy(modelItem))
            {
                Dictionary<string, object> hierarchyNode = new Dictionary<string, object>();
                hierarchyNode["displayName"] = SafeModelItemDisplayName(hierarchyItem);
                hierarchyNode["instanceGuid"] = GetInstanceGuid(hierarchyItem).ToString("D");
                hierarchyNode["properties"] = GetRelevantProperties(hierarchyItem);
                hierarchy.Add(hierarchyNode);
                hierarchyNames.Add(SafeModelItemDisplayName(hierarchyItem));
            }

            Dictionary<string, object> result = new Dictionary<string, object>();
            result["displayName"] = SafeModelItemDisplayName(modelItem);
            result["instanceGuid"] = instanceGuid.ToString("D");
            result["hasNonZeroInstanceGuid"] = instanceGuid != Guid.Empty;
            result["hierarchyPath"] = string.Join(" / ", hierarchyNames.ToArray());
            result["hierarchy"] = hierarchy;

            return result;
        }

        private static List<ModelItem> GetHierarchy(ModelItem modelItem)
        {
            List<ModelItem> items = new List<ModelItem>();
            ModelItem currentItem = modelItem;
            int safetyCounter = 0;

            while (currentItem != null && safetyCounter < 100)
            {
                items.Add(currentItem);
                currentItem = currentItem.Parent;
                safetyCounter++;
            }

            items.Reverse();
            return items;
        }

        private static List<object> GetRelevantProperties(ModelItem modelItem)
        {
            List<object> properties = new List<object>();

            if (modelItem == null)
                return properties;

            try
            {
                foreach (PropertyCategory category in modelItem.PropertyCategories)
                {
                    foreach (DataProperty property in category.Properties)
                    {
                        if (!IsRelevantProperty(category, property))
                            continue;

                        Dictionary<string, object> propertyInfo = new Dictionary<string, object>();
                        propertyInfo["categoryName"] = SafeString(category.Name);
                        propertyInfo["categoryDisplayName"] = SafeString(category.DisplayName);
                        propertyInfo["propertyName"] = SafeString(property.Name);
                        propertyInfo["propertyDisplayName"] = SafeString(property.DisplayName);
                        propertyInfo["value"] = GetPropertyValueText(property);
                        properties.Add(propertyInfo);
                    }
                }
            }
            catch
            {
                // One unavailable property should not prevent exporting the remaining sets.
            }

            return properties;
        }

        private static bool IsRelevantProperty(
            PropertyCategory category,
            DataProperty property)
        {
            string propertyName = SafeString(property.Name).Trim().ToLowerInvariant();
            string propertyDisplayName = SafeString(property.DisplayName).Trim().ToLowerInvariant();
            string combined = (
                SafeString(category.Name) + " " +
                SafeString(category.DisplayName) + " " +
                propertyName + " " +
                propertyDisplayName).ToLowerInvariant();

            string[] keywords = new string[]
            {
                "guid", "ifc", "identifier", "element id", "object id", "source",
                "file", "filename", "file name", "model", "name", "type", "category",
                "handle", "рабочий набор", "уровень", "объект id", "id объекта",
                "идентификатор", "файл", "источник", "модель", "имя", "название",
                "тип", "категория"
            };

            for (int index = 0; index < keywords.Length; index++)
            {
                if (combined.Contains(keywords[index]))
                    return true;
            }

            if (propertyName == "id" || propertyDisplayName == "id")
                return true;

            if (propertyName.EndsWith(" id") || propertyDisplayName.EndsWith(" id"))
                return true;

            return false;
        }

        private static string GetPropertyValueText(DataProperty property)
        {
            try
            {
                return property.Value.ToDisplayString() ?? "";
            }
            catch
            {
                try
                {
                    return property.Value.ToString() ?? "";
                }
                catch
                {
                    return "";
                }
            }
        }

        private static Guid GetInstanceGuid(ModelItem modelItem)
        {
            if (modelItem == null)
                return Guid.Empty;

            try
            {
                return modelItem.InstanceGuid;
            }
            catch
            {
                return Guid.Empty;
            }
        }

        private static string SafeModelItemDisplayName(ModelItem modelItem)
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

        private static string SafeSavedItemDisplayName(SavedItem item)
        {
            if (item == null)
                return "";

            try
            {
                return item.DisplayName ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static string SafeString(string value)
        {
            return value ?? "";
        }

        private static void ShowExportSummary(
            string filePath,
            int setCount,
            ExportStatistics statistics)
        {
            StringBuilder message = new StringBuilder();
            message.AppendLine("Диагностика Selection Sets экспортирована.");
            message.AppendLine();
            message.AppendLine("Файл:");
            message.AppendLine(filePath);
            message.AppendLine();
            message.AppendLine("Найдено Selection Sets: " + setCount);
            message.AppendLine("Всего выбранных узлов: " + statistics.TotalSelectedItems);
            message.AppendLine("Узлов с ненулевым InstanceGuid: " + statistics.ItemsWithNonZeroInstanceGuid);
            message.AppendLine("Узлов с нулевым InstanceGuid: " + statistics.ItemsWithZeroInstanceGuid);
            message.AppendLine();
            message.AppendLine("В JSON добавлены иерархические пути и ключевые свойства узлов.");
            message.AppendLine("NWF и сами наборы не изменялись.");

            MessageBox.Show(
                message.ToString(),
                "Navis Coordinator — Export Selection Sets",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        private sealed class ExportStatistics
        {
            public int TotalSelectedItems;
            public int ItemsWithNonZeroInstanceGuid;
            public int ItemsWithZeroInstanceGuid;
        }
    }
}
