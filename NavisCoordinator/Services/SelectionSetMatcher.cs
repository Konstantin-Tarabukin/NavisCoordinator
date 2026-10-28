using NavisCoordinator.Services;
using System.Windows.Forms;
using Autodesk.Navisworks.Api.Plugins;

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
                "НЕТ — перейти к сухой проверке или фактическому переносу Selection Sets.\n\n" +
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
                DialogResult operationChoice = MessageBox.Show(
                    "Выберите операцию:\n\n" +
                    "ДА — сухая проверка соответствий. NWF не изменяется.\n\n" +
                    "НЕТ — создать Selection Sets в текущем целевом NWF на основе исходного JSON.\n" +
                    "Перед изменением будет выполнена полная проверка и задан отдельный вопрос подтверждения.\n" +
                    "Используйте только рабочую КОПИЮ целевого NWF.\n\n" +
                    "ОТМЕНА — вернуться без изменений.",
                    "Navis Coordinator — операция",
                    MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Warning);

                if (operationChoice == DialogResult.Yes)
                {
                    SelectionSetMatcher.RunDryRun();
                }
                else if (operationChoice == DialogResult.No)
                {
                    SelectionSetMatcher.RunTransfer();
                }
            }

            return 0;
        }
    }
}
