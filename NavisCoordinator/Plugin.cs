using Autodesk.Navisworks.Api;
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
            Document doc = Autodesk.Navisworks.Api.Application.ActiveDocument;

            string text = ModelInspector.GetSelectedItemInfo(doc);

            MessageBox.Show(text);

            return 0;
        }
    }
}