using System.Text.Json;

namespace Eota.Content.Compiler;

internal static class JsonAuthoringValidator
{
    public static bool TryFindDuplicateProperty(JsonElement element, string path, out string duplicatePath)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                {
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in element.EnumerateObject())
                    {
                        var propertyPath = $"{path}.{property.Name}";
                        if (!names.Add(property.Name))
                        {
                            duplicatePath = propertyPath;
                            return true;
                        }

                        if (TryFindDuplicateProperty(property.Value, propertyPath, out duplicatePath))
                        {
                            return true;
                        }
                    }

                    break;
                }
            case JsonValueKind.Array:
                {
                    var index = 0;
                    foreach (var item in element.EnumerateArray())
                    {
                        if (TryFindDuplicateProperty(item, $"{path}[{index}]", out duplicatePath))
                        {
                            return true;
                        }

                        index++;
                    }

                    break;
                }
        }

        duplicatePath = string.Empty;
        return false;
    }
}
