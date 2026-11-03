using Autodesk.Revit.UI;

namespace Ubl.Engine.Revit;

/// <summary>Adds the UBL Engine tab: Tender Set (AR-21, setup check, family test) and Rooms (toilet).</summary>
public sealed class App : IExternalApplication
{
    private const string Tab = "UBL Engine";

    public Result OnStartup(UIControlledApplication app)
    {
        try { app.CreateRibbonTab(Tab); }
        catch (Autodesk.Revit.Exceptions.ArgumentException) { /* tab already exists */ }

        var panel = app.CreateRibbonPanel(Tab, "Tender Set");
        string assembly = typeof(App).Assembly.Location;
        var shell = new PushButtonData("UblBuildShell", "AR-21\nAs-Built Shell", assembly, "Ubl.Engine.Revit.Commands.BuildShellCommand")
        {
            ToolTip = "Reads the client's DWG, builds the existing shell in 3D (Existing phase) and creates sheet AR-21.",
        };
        panel.AddItem(shell);
        var check = new PushButtonData("UblCheckSetup", "Check\nSetup", assembly, "Ubl.Engine.Revit.Commands.CheckSetupCommand")
        {
            ToolTip = "Read-only: lists whether the families, materials, title block and phases the engine needs are available.",
        };
        panel.AddItem(check);
        var families = new PushButtonData("UblTestFamilies", "Test\nFamilies", assembly, "Ubl.Engine.Revit.Commands.TestFamiliesCommand")
        {
            ToolTip = "Tests every family in a folder: placement, insertion point, facing, footprint, rotate/move/mirror/flip accuracy, imported CAD, category.",
        };
        panel.AddItem(families);
        var toilets = app.CreateRibbonPanel(Tab, "Rooms");
        var toilet = new PushButtonData("UblBuildToilet", "Build\nToilet", assembly, "Ubl.Engine.Revit.Commands.BuildToiletCommand")
        {
            ToolTip = "Builds a complete toilet from one input: walls, door, floor, ceiling, and the client's fixtures planned by the rules and placed by measurement.",
        };
        toilets.AddItem(toilet);
        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication app) => Result.Succeeded;
}
