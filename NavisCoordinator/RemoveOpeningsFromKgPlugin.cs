using NavisCoordinator.Services;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Plugins;
using System;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace NavisCoordinator
{
    [Plugin(
        "RemoveOpeningsFromKg",
        "CT",
        DisplayName = "KG — исключить отверстия из Selection Set")]
    public class RemoveOpeningsFromKgPlugin : AddInPlugin
    {
        public override int Execute(params string[] parameters)
        {
            Document document = Autodesk.Navisworks.Api.Application.ActiveDocument;
            if (document == null || document.IsClear)
            {
                MessageBox.Show(
                    "Сначала откройте NWF с загруженной моделью и Selection Set KG.",
                    "Navis Coordinator — KG",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return 0;
            }

            SelectionSetOpeningCleaner.CleanupPlan plan;
            try
            {
                plan = SelectionSetOpeningCleaner.CreatePlan(document);
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    "Dry Run не выполнен. Документ не изменён.\n\n" + exception.Message,
                    "Navis Coordinator — KG / очистка отверстий",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return 0;
            }

            string report = SelectionSetOpeningCleaner.BuildReport(plan);
            string reportPath = TryWriteReport(report);
            string preview = BuildPreview(plan, reportPath);

            if (!plan.CanApply)
            {
                MessageBox.Show(
                    preview + "\n\nПрименять нечего. Selection Set не изменён.",
                    "Navis Coordinator — KG / Dry Run",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return 0;
            }

            DialogResult choice = MessageBox.Show(
                preview + "\n\n" +
                "ДА — применить очистку только к Selection Set KG.\n" +
                "НЕТ — оставить документ без изменений (только Dry Run).\n" +
                "ОТМЕНА — выйти.",
                "Navis Coordinator — KG / подтверждение",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Warning);

            if (choice != DialogResult.Yes)
                return 0;

            try
            {
                SelectionSetOpeningCleaner.Apply(plan);
                MessageBox.Show(
                    "Очистка KG применена.\n\n" +
                    "Исключено веток отверстий: " + plan.RemovedNodePaths.Count + "\n" +
                    "Элементов верхнего уровня до: " + plan.OriginalItemCount + "\n" +
                    "Элементов верхнего уровня после: " + plan.CleanedItemCount + "\n\n" +
                    "Остальные Selection Sets и Clash Tests не изменялись.\n" +
                    "NWF автоматически не сохранён. Проверьте тесты, использующие KG, затем сохраните копию NWF.",
                    "Navis Coordinator — KG / завершено",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    "Не удалось завершить очистку KG.\n\n" + exception.Message + "\n\n" +
                    "Проверьте Selection Set KG в открытом документе. NWF не сохраняйте, " +
                    "пока не убедитесь, что исходный набор восстановлен.",
                    "Navis Coordinator — KG / ошибка",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }

            return 0;
        }

        private static string BuildPreview(
            SelectionSetOpeningCleaner.CleanupPlan plan,
            string reportPath)
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("Проверен только Selection Set KG.");
            builder.AppendLine("Найдено категорий 'Обобщенные модели': " + plan.GenericModelsCategoryCount);
            builder.AppendLine("Просмотрено узлов дерева: " + plan.NodesInspected);
            builder.AppendLine("Найдено веток с 'Отверст': " + plan.RemovedNodePaths.Count);
            builder.AppendLine("Элементов верхнего уровня: " + plan.OriginalItemCount + " → " + plan.CleanedItemCount);
            builder.AppendLine();

            int previewCount = Math.Min(plan.RemovedNodePaths.Count, 15);
            for (int index = 0; index < previewCount; index++)
            {
                builder.Append("• ");
                builder.AppendLine(plan.RemovedNodePaths[index]);
            }
            if (plan.RemovedNodePaths.Count > previewCount)
            {
                builder.AppendLine("… ещё " + (plan.RemovedNodePaths.Count - previewCount) + " совпадений — полный список в отчёте.");
            }
            if (!string.IsNullOrWhiteSpace(reportPath))
                builder.AppendLine("Полный отчёт: " + reportPath);

            return builder.ToString();
        }

        private static string TryWriteReport(string report)
        {
            try
            {
                string directory = Path.Combine(Path.GetTempPath(), "NavisCoordinator");
                Directory.CreateDirectory(directory);
                string fileName = "KG_OpeningCleanup_DryRun_" +
                                  DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt";
                string path = Path.Combine(directory, fileName);
                File.WriteAllText(path, report, new UTF8Encoding(true));
                return path;
            }
            catch
            {
                return "";
            }
        }
    }
}
