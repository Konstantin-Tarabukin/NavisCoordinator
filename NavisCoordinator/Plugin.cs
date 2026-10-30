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
                "НЕТ — перейти к проверке/переносу Selection Sets.\n\n" +
                "ОТМЕНА — ничего не делать.",
                "Navis Coordinator",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Question);

            if (choice == DialogResult.Yes)
            {
                SelectionSetExporter.ExportSelectionSets();
                return 0;
            }

            if (choice != DialogResult.No)
                return 0;

            DialogResult workflowChoice = MessageBox.Show(
                "Выберите режим:\n\n" +
                "ДА — нативная пересборка по правилам разделов и фактическому дереву модели.\n" +
                "Используется универсальный профиль UniversalNativeRebuildProfile_v1.json; разделы и корни обнаруживаются в текущем NWF.\n\n" +
                "НЕТ — перенос по legacy JSON, как в предыдущих версиях.\n\n" +
                "ОТМЕНА — выйти без изменений.",
                "Navis Coordinator — режим",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Question);

            if (workflowChoice == DialogResult.Cancel)
                return 0;

            if (workflowChoice == DialogResult.Yes)
            {
                DialogResult nativeOperationChoice = MessageBox.Show(
                    "Нативная пересборка Selection Sets:\n\n" +
                    "ДА — только Dry Run; документ не изменяется.\n\n" +
                    "НЕТ — применить пересборку в памяти после успешного preflight и дополнительного подтверждения.\n" +
                    "Только на рабочей КОПИИ NWF. Автоматического сохранения не будет.\n\n" +
                    "ОТМЕНА — выйти.",
                    "Navis Coordinator — native rebuild",
                    MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Warning);

                if (nativeOperationChoice == DialogResult.Yes)
                    SelectionSetMatcher.RunNativeRebuildDryRun();
                else if (nativeOperationChoice == DialogResult.No)
                    SelectionSetMatcher.RunNativeRebuildTransfer();

                return 0;
            }

            DialogResult legacyOperationChoice = MessageBox.Show(
                "Legacy-режим по JSON:\n\n" +
                "ДА — сухая проверка соответствий. NWF не изменяется.\n\n" +
                "НЕТ — создать Selection Sets из выбранного исходного JSON. Существующие наборы не перезаписываются.\n" +
                "Перед изменением будет выполнена полная проверка и задан отдельный вопрос подтверждения.\n" +
                "Используйте только рабочую КОПИЮ целевого NWF.\n\n" +
                "ОТМЕНА — выйти без изменений.",
                "Navis Coordinator — legacy JSON",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Warning);

            if (legacyOperationChoice == DialogResult.Yes)
                SelectionSetMatcher.RunDryRun();
            else if (legacyOperationChoice == DialogResult.No)
                SelectionSetMatcher.RunTransfer();

            return 0;
        }
    }
}
