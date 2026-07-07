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
            SearchSetService.CreateTestSearchSet();

            return 0;
        }
    }
}