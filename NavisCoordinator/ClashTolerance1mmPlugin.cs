using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Drawing;
using System.Windows.Forms;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;
using Autodesk.Navisworks.Api.Plugins;

namespace NavisCoordinator
{
    /// <summary>
    /// Sets a user-defined tolerance, entered in metres, on every existing Clash Detective test.
    /// Does not rename tests and does not change Selection A / Selection B.
    /// The tolerance is converted to the active document's units.
    /// </summary>
    [Plugin(
        "ClashTolerance1mm",
        "CT",
        DisplayName = "Clash Detective — произвольный допуск (м)")]
    public class ClashTolerance1mmPlugin : AddInPlugin
    {
        // Keep the existing internal plugin ID and class name so the current command registration
        // does not need to be changed. Only the displayed command name and input behavior change.
        private const double DefaultToleranceMeters = 0.001;

        public override int Execute(params string[] parameters)
        {
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

            // Ask for the value before changing anything in the document.
            double toleranceMeters;
            if (!TryPromptToleranceMeters(out toleranceMeters))
            {
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

                toleranceInDocumentUnits = toleranceMeters / metersPerDocumentUnit;

                if (double.IsNaN(toleranceInDocumentUnits) ||
                    double.IsInfinity(toleranceInDocumentUnits) ||
                    toleranceInDocumentUnits <= 0.0)
                {
                    throw new InvalidOperationException(
                        "Получено недопустимое значение допуска после пересчёта единиц.");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Не удалось пересчитать введённый допуск в единицы документа " +
                    document.Units + ":\n\n" + ex.Message,
                    "Clash Detective — ошибка единиц",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return 0;
            }

            string toleranceMetersText = toleranceMeters.ToString(
                "0.################",
                CultureInfo.CurrentCulture);

            string toleranceMillimetersText = (toleranceMeters * 1000.0).ToString(
                "0.################",
                CultureInfo.CurrentCulture);

            DialogResult confirmation = MessageBox.Show(
                "Будут изменены допуски у " + tests.Count + " проверок Clash Detective.\n\n" +
                "Новый допуск: " + toleranceMetersText + " м (" +
                toleranceMillimetersText + " мм).\n" +
                "Единицы документа: " + document.Units + ".\n" +
                "Значение, записываемое через API: " +
                toleranceInDocumentUnits.ToString("0.################", CultureInfo.InvariantCulture) +
                " (единицы документа).\n\n" +
                "Названия тестов и Selection A / Selection B НЕ изменяются.\n" +
                "Типы тестов и правила также остаются без изменений.\n" +
                "После изменения допуска результаты потребуется пересчитать.\n" +
                "NWF автоматически не сохраняется.\n\n" +
                "Продолжить?",
                "Clash Detective — произвольный допуск",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (confirmation != DialogResult.Yes)
            {
                return 0;
            }

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
                    "Допуск: " + toleranceMetersText + " м (" +
                    toleranceMillimetersText + " мм).\n" +
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

        private static bool TryPromptToleranceMeters(out double toleranceMeters)
        {
            toleranceMeters = 0.0;
            double selectedToleranceMeters = 0.0;

            using (Form form = new Form())
            {
                Label promptLabel = new Label();
                TextBox toleranceTextBox = new TextBox();
                Label unitLabel = new Label();
                Label hintLabel = new Label();
                Button applyButton = new Button();
                Button cancelButton = new Button();
                form.Text = "Clash Detective — допуск";
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.StartPosition = FormStartPosition.CenterScreen;
                form.ClientSize = new Size(370, 150);
                form.MaximizeBox = false;
                form.MinimizeBox = false;
                form.ShowInTaskbar = false;

                promptLabel.AutoSize = true;
                promptLabel.Text = "Введите новый допуск:";
                promptLabel.Location = new Point(16, 16);

                toleranceTextBox.Location = new Point(19, 43);
                toleranceTextBox.Size = new Size(245, 23);
                toleranceTextBox.Text = DefaultToleranceMeters.ToString(
                    "0.################",
                    CultureInfo.CurrentCulture);

                unitLabel.AutoSize = true;
                unitLabel.Text = "м";
                unitLabel.Location = new Point(274, 46);

                hintLabel.AutoSize = true;
                hintLabel.Text = "Например: 0,005 м = 5 мм. Допускаются точка и запятая.";
                hintLabel.Location = new Point(19, 76);

                applyButton.Text = "Продолжить";
                applyButton.Size = new Size(105, 28);
                applyButton.Location = new Point(143, 108);

                cancelButton.Text = "Отмена";
                cancelButton.Size = new Size(90, 28);
                cancelButton.Location = new Point(258, 108);
                cancelButton.DialogResult = DialogResult.Cancel;

                applyButton.Click += delegate
                {
                    double parsedValue;
                    if (!TryParseToleranceMeters(toleranceTextBox.Text, out parsedValue))
                    {
                        MessageBox.Show(
                            form,
                            "Введите положительное число в метрах.\n\n" +
                            "Примеры: 0,001; 0.005; 0,01",
                            "Некорректный допуск",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);

                        toleranceTextBox.Focus();
                        toleranceTextBox.SelectAll();
                        return;
                    }

                    selectedToleranceMeters = parsedValue;
                    form.DialogResult = DialogResult.OK;
                };

                form.Controls.Add(promptLabel);
                form.Controls.Add(toleranceTextBox);
                form.Controls.Add(unitLabel);
                form.Controls.Add(hintLabel);
                form.Controls.Add(applyButton);
                form.Controls.Add(cancelButton);
                form.AcceptButton = applyButton;
                form.CancelButton = cancelButton;

                form.Shown += delegate
                {
                    toleranceTextBox.Focus();
                    toleranceTextBox.SelectAll();
                };

                if (form.ShowDialog() != DialogResult.OK)
                {
                    return false;
                }

                toleranceMeters = selectedToleranceMeters;
                return true;
            }
        }

        private static bool TryParseToleranceMeters(string input, out double toleranceMeters)
        {
            toleranceMeters = 0.0;

            if (string.IsNullOrWhiteSpace(input))
            {
                return false;
            }

            // Accept both Russian decimal comma and invariant decimal point.
            string normalizedInput = input.Trim().Replace(',', '.');

            double parsedValue;
            bool parsed = double.TryParse(
                normalizedInput,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out parsedValue);

            if (!parsed ||
                double.IsNaN(parsedValue) ||
                double.IsInfinity(parsedValue) ||
                parsedValue <= 0.0)
            {
                return false;
            }

            toleranceMeters = parsedValue;
            return true;
        }
    }
}
