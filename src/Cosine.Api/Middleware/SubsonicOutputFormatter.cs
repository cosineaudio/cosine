using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Xml;
using Cosine.Api.Generated;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;

namespace Cosine.Api.Middleware;

/// <summary>
/// Serializes Subsonic API responses. The generated response types already model the
/// complete <c>subsonic-response</c> envelope (status, version, payload) with the correct
/// JSON property names, so this formatter serializes the response object directly rather
/// than wrapping it again. XML is produced from the same JSON node tree using the Subsonic
/// convention: scalar values become attributes, nested objects/arrays become child elements.
/// </summary>
public class SubsonicOutputFormatter : IOutputFormatter
{
    private const string SubsonicNamespace = "http://subsonic.org/restapi";

    /// <summary>
    /// How a generated Subsonic type is written, wherever it is written.
    /// <para>
    /// Shared rather than private because the generated DTOs are not self-describing: a
    /// <c>Child</c> serialized with stock options silently loses <c>size</c> (see
    /// <see cref="WidenChildSize"/>) and writes enums by member name instead of their
    /// <c>[EnumMember]</c> value. Endpoints outside <c>/rest</c> that return one of these types
    /// - cosine's own extensions, which the formatter deliberately does not claim - reuse this
    /// so a song object is identical whichever surface produced it.
    /// </para>
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // The generated enums carry [EnumMember] values (e.g. "ok") that System.Text.Json's
        // default JsonStringEnumConverter ignores. Force an EnumMember-aware converter onto
        // every enum property so both JSON and XML emit the spec-correct values.
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers = { ApplyEnumMemberConverters, WidenChildSize }
        }
    };

    public bool CanWriteResult(OutputFormatterCanWriteContext context)
    {
        return context.Object is not null
            && context.HttpContext.Request.Path.StartsWithSegments("/rest", StringComparison.OrdinalIgnoreCase);
    }

    public async Task WriteAsync(OutputFormatterWriteContext context)
    {
        var data = context.Object;
        if (data is ObjectResult objectResult)
            data = objectResult.Value;

        var wantsJson = string.Equals(
            context.HttpContext.Request.Query["f"].ToString(),
            "json",
            StringComparison.OrdinalIgnoreCase);

        if (wantsJson)
        {
            var json = JsonSerializer.Serialize(data, data?.GetType() ?? typeof(object), JsonOptions);
            context.HttpContext.Response.ContentType = "application/json; charset=utf-8";
            await context.HttpContext.Response.WriteAsync(json);
        }
        else
        {
            var node = JsonSerializer.SerializeToNode(data, data?.GetType() ?? typeof(object), JsonOptions);
            context.HttpContext.Response.ContentType = "application/xml; charset=utf-8";
            await context.HttpContext.Response.WriteAsync(RenderXml(node));
        }
    }

    private static string RenderXml(JsonNode? root)
    {
        using var stringWriter = new StringWriter();
        using (var xml = XmlWriter.Create(stringWriter, new XmlWriterSettings { OmitXmlDeclaration = true, Indent = true }))
        {
            // The envelope has a single top-level property ("subsonic-response") whose value
            // is the response object; that property becomes the root element.
            if (root is JsonObject obj)
            {
                foreach (var (name, value) in obj)
                    WriteElement(xml, name, value);
            }
        }
        return stringWriter.ToString();
    }

    private static void WriteElement(XmlWriter xml, string name, JsonNode? node)
    {
        if (node is null)
            return;

        switch (node)
        {
            case JsonArray array:
                // Arrays repeat the element name once per item.
                foreach (var item in array)
                    WriteElement(xml, name, item);
                break;

            case JsonObject obj:
                xml.WriteStartElement(name, SubsonicNamespace);
                // XML requires all attributes before any child element: scalars first...
                foreach (var (childName, childValue) in obj)
                {
                    if (IsScalar(childValue))
                        xml.WriteAttributeString(childName, ScalarToString(childValue!));
                }
                // ...then nested objects/arrays as child elements.
                foreach (var (childName, childValue) in obj)
                {
                    if (childValue is not null && !IsScalar(childValue))
                        WriteElement(xml, childName, childValue);
                }
                xml.WriteEndElement();
                break;

            default: // JsonValue - a scalar standing on its own becomes an element with text.
                xml.WriteStartElement(name, SubsonicNamespace);
                xml.WriteString(ScalarToString(node));
                xml.WriteEndElement();
                break;
        }
    }

    private static bool IsScalar(JsonNode? node)
        => node is JsonValue;

    private static string ScalarToString(JsonNode node) => node.GetValueKind() switch
    {
        JsonValueKind.String => node.GetValue<string>(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => node.ToJsonString(),
        _ => node.ToString()
    };

    private static void ApplyEnumMemberConverters(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object)
            return;

        foreach (var property in typeInfo.Properties)
        {
            // A CustomConverter must match the property type exactly, so nullable enum
            // properties need a Nullable-typed converter (not the underlying-enum one).
            var underlying = Nullable.GetUnderlyingType(property.PropertyType);
            if (underlying is not null && underlying.IsEnum)
                property.CustomConverter = EnumMemberConverter.ForNullableType(underlying);
            else if (property.PropertyType.IsEnum)
                property.CustomConverter = EnumMemberConverter.ForType(property.PropertyType);
        }
    }

    /// <summary>
    /// Serializes <c>Child.size</c> (and its subclasses') from <see cref="Child.FileSize"/>, a
    /// <c>long?</c>, instead of the generated <c>int?</c> - which truncates silently for files
    /// over 2 GB. The spec types the field as int32 and is not ours to edit, so the width is
    /// corrected here, in the serializer every surface shares - <see cref="Child.FileSize"/> is
    /// <c>[JsonIgnore]</c>, so a <c>Child</c> written with any other options emits no size at
    /// all. Falls back to the generated property so a caller that only set <c>Size</c> still
    /// emits a size.
    /// </summary>
    private static void WidenChildSize(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object || !typeof(Child).IsAssignableFrom(typeInfo.Type))
            return;

        var generated = typeInfo.Properties.FirstOrDefault(p => p.Name == "size");
        if (generated is null)
            return;

        var widened = typeInfo.CreateJsonPropertyInfo(typeof(long?), "size");
        widened.Get = obj => ((Child)obj).FileSize ?? ((Child)obj).Size;
        // Custom property infos don't inherit the options' WhenWritingNull condition.
        widened.ShouldSerialize = (_, value) => value is not null;
        typeInfo.Properties[typeInfo.Properties.IndexOf(generated)] = widened;
    }
}

/// <summary>Non-generic entry points that build EnumMember-aware converters for a given enum type.</summary>
internal static class EnumMemberConverter
{
    public static JsonConverter ForType(Type enumType)
        => (JsonConverter)Activator.CreateInstance(
            typeof(EnumMemberConverter<>).MakeGenericType(enumType))!;

    public static JsonConverter ForNullableType(Type enumType)
        => (JsonConverter)Activator.CreateInstance(
            typeof(NullableEnumMemberConverter<>).MakeGenericType(enumType))!;
}

/// <summary>Wraps <see cref="EnumMemberConverter{T}"/> for <c>T?</c> properties.</summary>
internal sealed class NullableEnumMemberConverter<T> : JsonConverter<T?> where T : struct, Enum
{
    private static readonly EnumMemberConverter<T> Inner = new();

    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Null ? null : Inner.Read(ref reader, typeof(T), options);

    public override void Write(Utf8JsonWriter writer, T? value, JsonSerializerOptions options)
    {
        if (value is null)
            writer.WriteNullValue();
        else
            Inner.Write(writer, value.Value, options);
    }
}

/// <summary>
/// String enum converter that honors <see cref="EnumMemberAttribute"/> values (which the
/// built-in <c>JsonStringEnumConverter</c> ignores), falling back to the member name.
/// </summary>
internal sealed class EnumMemberConverter<T> : JsonConverter<T> where T : struct, Enum
{
    private static readonly Dictionary<T, string> ToName = Build();

    private static Dictionary<T, string> Build()
    {
        var map = new Dictionary<T, string>();
        foreach (var field in typeof(T).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var value = (T)field.GetValue(null)!;
            var member = field.GetCustomAttribute<EnumMemberAttribute>();
            map[value] = member?.Value ?? field.Name;
        }
        return map;
    }

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        if (text is not null)
        {
            foreach (var (value, name) in ToName)
            {
                if (string.Equals(name, text, StringComparison.OrdinalIgnoreCase))
                    return value;
            }
            if (Enum.TryParse<T>(text, ignoreCase: true, out var parsed))
                return parsed;
        }
        return default;
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        => writer.WriteStringValue(ToName.TryGetValue(value, out var name) ? name : value.ToString());
}
