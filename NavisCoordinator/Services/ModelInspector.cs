using Autodesk.Navisworks.Api;
using System.Text;

namespace NavisCoordinator.Services
{
    public static class ModelInspector
    {
        public static string GetSelectedItemInfo(Document doc)
        {
            if (doc.CurrentSelection.SelectedItems.Count == 0)
                return "Ничего не выбрано.";

            ModelItem item = doc.CurrentSelection.SelectedItems[0];

            StringBuilder sb = new StringBuilder();

            sb.AppendLine("=== Property Categories ===");
            sb.AppendLine();

            foreach (PropertyCategory category in item.PropertyCategories)
            {
                sb.AppendLine(category.DisplayName);
            }

            return sb.ToString();
        }
    }
}