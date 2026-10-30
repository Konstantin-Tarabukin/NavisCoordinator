using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;
using Autodesk.Navisworks.Api.Plugins;

namespace NavisCoordinator
{
    /// <summary>
    /// Sets a 1 mm tolerance on every existing Clash Detective test.
    /// Does not rename tests and does not change Selection A / Selection B.
    /// The tolerance is converted to the active document's units.
    /// </summary>
    [Plugin(
        "ClashTolerance1mm",
        "CT",
        DisplayName = "Clash Detective — допуск 1 мм (без изменения пар)")]
    public class ClashTolerance1mmPlugin : AddInPlugin
    {
        private const double ToleranceMeters = 0.001;

        public override int Execute(params string[] parameters)
        {
            // Fully qualify Application: System.Windows.Forms.Application
            // and Autodesk.Navisworks.Api.Application otherwise conflict.
            Document document = Autodesk.Navisworks.Api.Application.ActiveDocument;

            if (document == null)
            {
                MessageBox.Show(
                    "Нет открытого документа Navisworks.",
                    "Clash Detective",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return 0;
            }

            DocumentClash documentClash;
            try
            {
                documentClash = document.GetClash();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Не удалось получить Clash Detective:\n\n" + ex.Message,
                    "Clash Detective",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return 0;
            }

            if (documentClash == null || documentClash.TestsData == null)
            {
                MessageBox.Show(
                    "В документе не найден Clash Detective.",
                    "Clash Detective",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return 0;
            }

            List<ClashTest> tests = documentClash.TestsData.Tests
                .OfType<ClashTest>()
                .ToList();

            if (tests.Count == 0)
            {
                MessageBox.Show(
                    "В Clash Detective не найдено ни одной проверки.",
                    "Clash Detective",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return 0;
            }

            double metersPerDocumentUnit;
            double toleranceInDocumentUnits;

            try
            {
                // ClashTest.Tolerance is expressed in the active document's units.
                // ScaleFactor(from, to) returns the size of one source unit
                // expressed in the destination unit: here metres per document unit.
                metersPerDocumentUnit = UnitConversion.ScaleFactor(
                    document.Units,
                    Units.Meters);

                if (double.IsNaN(metersPerDocumentUnit) ||
                    double.IsInfinity(metersPerDocumentUnit) ||
                    metersPerDocumentUnit <= 0.0)
                {
                    throw new InvalidOperationException(
                        "Не удалось определить коэффициент единиц документа: " +
                        document.Units + ".");
                }

                toleranceInDocumentUnits = ToleranceMeters / metersPerDocumentUnit;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Не удалось пересчитать допуск 1 мм в единицы документа " +
                    document.Units + ":\n\n" + ex.Message,
                    "Clash Detective — ошибка единиц",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return 0;
            }

            DialogResult confirmation = MessageBox.Show(
                "Будут изменены допуски у " + tests.Count + " проверок Clash Detective.\n\n" +
                "Новый допуск: 0,001 м (1 мм).\n" +
                "Единицы документа: " + document.Units + ".\n" +
                "Значение, записываемое через API: " +
                toleranceInDocumentUnits.ToString("0.################", CultureInfo.InvariantCulture) +
                " (единицы документа).\n\n" +
                "Названия тестов и Selection A / Selection B НЕ изменяются.\n" +
                "Типы тестов и правила также остаются без изменений.\n" +
                "После изменения допуска результаты потребуется пересчитать.\n" +
                "NWF автоматически не сохраняется.\n\n" +
                "Продолжить?",
                "Clash Detective — допуск 1 мм",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (confirmation != DialogResult.Yes)
                return 0;

            int updated = 0;
            try
            {
                foreach (ClashTest originalTest in tests)
                {
                    ClashTest editedTest = originalTest.CreateCopy() as ClashTest;
                    if (editedTest == null)
                    {
                        throw new InvalidOperationException(
                            "Не удалось создать копию проверки '" +
                            originalTest.DisplayName + "'.");
                    }

                    editedTest.Tolerance = toleranceInDocumentUnits;

                    documentClash.TestsData.TestsEditTestFromCopy(
                        originalTest,
                        editedTest);

                    updated++;
                }

                MessageBox.Show(
                    "Готово. Обновлён допуск у " + updated + " из " + tests.Count +
                    " проверок.\n\n" +
                    "Допуск: 1 мм.\n" +
                    "Названия и стороны Selection A / Selection B не изменялись.\n" +
                    "Проверки не запускались; NWF автоматически не сохранялся.",
                    "Clash Detective — завершено",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Обработка остановлена. Успешно изменено: " + updated +
                    " из " + tests.Count + " проверок.\n\n" +
                    "Ошибка:\n" + ex.Message + "\n\n" +
                    "Изменения до ошибки могли остаться в открытом документе.\n" +
                    "Проверь список тестов и не сохраняй NWF, пока не проверишь результат.",
                    "Clash Detective — ошибка",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }

            return 0;
        }
    }
}
