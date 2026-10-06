using System.ComponentModel;
using System.Globalization;
using System.Reflection;

namespace ImageCoreService;

/// <summary>
/// Shows enum values by their [Description] (Vietnamese labels) in the settings property
/// grids, while XML serialization keeps the stable identifier names.
/// </summary>
public sealed class EnumDescriptionConverter(Type type) : EnumConverter(type)
{
    public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType)
    {
        if (destinationType == typeof(string) && value != null && value.GetType().IsEnum)
            return Describe(value);
        return base.ConvertTo(context, culture, value, destinationType);
    }

    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
    {
        if (value is string s)
        {
            foreach (object v in Enum.GetValues(EnumType))
                if (Describe(v) == s) return v;
        }
        return base.ConvertFrom(context, culture, value);
    }

    private string Describe(object value)
    {
        FieldInfo? f = EnumType.GetField(value.ToString()!);
        return f?.GetCustomAttribute<DescriptionAttribute>()?.Description ?? value.ToString()!;
    }
}
