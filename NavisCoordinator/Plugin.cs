using Autodesk.Navisworks.Api.Plugins;
using NavisCoordinator.Services;
using System.Windows.Forms;

namespace NavisCoordinator
{
    [Plugin(
        "NavisCoordinator",
        "CT",
        DisplayName = "Navis Coordinator")]
    public class Plugin : AddInPlugin
    {
        public override int Execute(params string[] parameters)
        {
            DialogResult choice = MessageBox.Show(
                "Выберите действие:\n\n" +
                "ДА — экспортировать Selection Sets из открытого исходного NWF в JSON.\n\n" +
                "НЕТ — выполнить сухую проверку соответствий: сравнить узлы из ранее экспортированного JSON с текущей моделью. Документ не изменяется.\n\n" +
                "ОТМЕНА — ничего не делать.",
                "Navis Coordinator",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Question);

            if (choice == DialogResult.Yes)
            {
                SelectionSetExporter.ExportSelectionSets();
            }
            else if (choice == DialogResult.No)
            {
                SelectionSetMatcher.RunDryRun();
            }

            return 0;
        }
    }
}
