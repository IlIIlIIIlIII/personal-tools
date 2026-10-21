using Windows.ApplicationModel.AppExtensions;
using Windows.Foundation.Collections;

var extensions = await AppExtensionCatalog.Open("com.microsoft.windows.widgets").FindAllAsync();
var provider = extensions.Single(x => x.Package.Id.Name == "PersonalTools.HdrWidget");
var properties = await provider.GetExtensionPropertiesAsync();
var root = (IPropertySet)properties["WidgetProvider"];
var definitions = (object[])((IPropertySet)root["Definitions"])["Definition"];
var expected = new HashSet<string> { "PersonalToolsHdr", "PersonalToolsDisplayPower", "PersonalToolsVllm" };
foreach (IPropertySet definition in definitions)
{
    string id = (string)definition["@Id"];
    if (!expected.Remove(id)) throw new Exception("Unexpected/duplicate widget: " + id);
    var themes = (IPropertySet)definition["ThemeResources"];
    var screenshot = (IPropertySet)((IPropertySet)themes["Screenshots"])["Screenshot"];
    var icon = (IPropertySet)((IPropertySet)themes["Icons"])["Icon"];
    foreach (var asset in new[] { screenshot, icon })
        if (!File.Exists(Path.Combine(provider.Package.InstalledLocation.Path, (string)asset["@Path"])))
            throw new Exception("Missing installed widget asset: " + asset["@Path"]);
    Console.WriteLine("PASS Windows app-extension catalog: " + definition["@DisplayName"] + " + installed assets");
}
if (expected.Count != 0) throw new Exception("Windows catalog missing: " + string.Join(", ", expected));
Console.WriteLine(provider.Package.Id.FullName + " | " + provider.DisplayName);
Console.WriteLine("Catalog and package checks do not replace a widget-picker UI check.");
