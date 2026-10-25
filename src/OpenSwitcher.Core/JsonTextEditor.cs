using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenSwitcher.Core;

internal sealed class JsonTextEditor
{
    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private readonly string _lineEnding;
    private readonly string _indentUnit;
    private byte[] _utf8;

    public JsonTextEditor(string text, string lineEnding)
    {
        _lineEnding = lineEnding;
        _utf8 = Encoding.UTF8.GetBytes(string.IsNullOrWhiteSpace(text) ? "{}" + lineEnding : text);
        _indentUnit = DetectIndentUnit();
    }

    public bool IsChanged { get; private set; }

    public string Text => Encoding.UTF8.GetString(_utf8);

    public void Set(IReadOnlyList<string> path, JsonNode value, bool insertFirst = false)
    {
        var property = FindProperty(path);
        if (property is not null)
        {
            var indent = GetLineIndent(property.NameStart);
            Splice(property.ValueStart, property.ValueEnd, Serialize(value, indent));
            return;
        }

        var existingDepth = 0;
        var container = ReadRoot();
        while (existingDepth < path.Count - 1)
        {
            var child = container.Find(path[existingDepth]);
            if (child is null)
            {
                break;
            }

            container = ReadObjectValue(child, path.Take(existingDepth + 1));
            existingDepth++;
        }

        var nestedValue = value;
        for (var index = path.Count - 1; index > existingDepth; index--)
        {
            nestedValue = new JsonObject { [path[index]] = nestedValue };
        }

        InsertProperty(container, path[existingDepth], nestedValue, insertFirst);
    }

    public void Remove(IReadOnlyList<string> path)
    {
        if (path.Count == 0)
        {
            throw new ArgumentException("The root object cannot be removed.", nameof(path));
        }

        var container = path.Count == 1 ? ReadRoot() : FindObject(path.Take(path.Count - 1).ToArray());
        var property = container?.Find(path[^1]);
        if (container is null || property is null)
        {
            return;
        }

        var index = container.Properties.IndexOf(property);
        if (container.Properties.Count == 1)
        {
            Splice(container.Start + 1, container.End - 1, string.Empty);
            return;
        }

        if (index > 0 && index == container.Properties.Count - 1)
        {
            Splice(container.Properties[index - 1].ValueEnd, property.ValueEnd, string.Empty);
            return;
        }

        var commaIndex = FindNextSignificant(property.ValueEnd);
        var removeEnd = commaIndex + 1;
        var lineEnd = SkipInlineWhitespace(removeEnd);
        if (lineEnd < _utf8.Length && (_utf8[lineEnd] == (byte)'\r' || _utf8[lineEnd] == (byte)'\n'))
        {
            removeEnd = SkipLineEnding(lineEnd);
        }

        Splice(GetRemovalStart(property.NameStart), removeEnd, string.Empty);
    }

    public void Rename(IReadOnlyList<string> path, string newName)
    {
        var property = FindProperty(path)
            ?? throw new InvalidOperationException($"'{string.Join('.', path)}' does not exist.");
        Splice(property.NameStart, property.NameEnd, Quote(newName));
    }

    private void InsertProperty(JsonObjectSpan container, string name, JsonNode value, bool insertFirst)
    {
        var containerIndent = GetLineIndent(container.Start);
        var propertyIndent = containerIndent + _indentUnit;
        var propertyText = $"{Quote(name)}: {Serialize(value, propertyIndent)}";

        if (insertFirst && container.Properties.Count > 0)
        {
            var firstNameStart = container.Properties[0].NameStart;
            var firstLineStart = GetRemovalStart(firstNameStart);
            if (firstLineStart != firstNameStart)
            {
                Splice(firstLineStart, firstLineStart, GetLineIndent(firstNameStart) + propertyText + "," + _lineEnding);
            }
            else
            {
                Splice(firstNameStart, firstNameStart, propertyText + ", ");
            }

            return;
        }

        if (container.Properties.Count == 0)
        {
            var isBlank = _utf8.AsSpan(container.Start + 1, container.End - container.Start - 2)
                .Trim(" \t\r\n"u8)
                .IsEmpty;
            if (isBlank)
            {
                Splice(container.Start + 1, container.End - 1, _lineEnding + propertyIndent + propertyText + _lineEnding + containerIndent);
            }
            else
            {
                Splice(container.Start + 1, container.Start + 1, _lineEnding + propertyIndent + propertyText);
            }

            return;
        }

        var closingBrace = container.End - 1;
        var closingLineStart = GetLineStart(closingBrace);
        var closingOnOwnLine = _utf8.AsSpan(closingLineStart, closingBrace - closingLineStart).Trim(" \t"u8).IsEmpty;
        if (closingOnOwnLine)
        {
            Splice(closingLineStart, closingLineStart, propertyIndent + propertyText + _lineEnding);
        }
        else
        {
            Splice(closingBrace, closingBrace, _lineEnding + propertyIndent + propertyText + _lineEnding + containerIndent);
        }

        var lastValueEnd = container.Properties[^1].ValueEnd;
        if (_utf8[FindNextSignificant(lastValueEnd)] != (byte)',')
        {
            Splice(lastValueEnd, lastValueEnd, ",");
        }
    }

    private JsonPropertySpan? FindProperty(IReadOnlyList<string> path)
    {
        if (path.Count == 0)
        {
            throw new ArgumentException("A property path is required.", nameof(path));
        }

        var container = path.Count == 1 ? ReadRoot() : FindObject(path.Take(path.Count - 1).ToArray());
        return container?.Find(path[^1]);
    }

    private JsonObjectSpan? FindObject(IReadOnlyList<string> path)
    {
        var container = ReadRoot();
        for (var index = 0; index < path.Count; index++)
        {
            var property = container.Find(path[index]);
            if (property is null)
            {
                return null;
            }

            container = ReadObjectValue(property, path.Take(index + 1));
        }

        return container;
    }

    private JsonObjectSpan ReadObjectValue(JsonPropertySpan property, IEnumerable<string> path)
    {
        if (_utf8[property.ValueStart] != (byte)'{')
        {
            throw new InvalidDataException($"'{string.Join('.', path)}' must be a JSON object.");
        }

        return ReadObject(property.ValueStart, property.ValueEnd);
    }

    private JsonObjectSpan ReadRoot()
    {
        var reader = new Utf8JsonReader(_utf8, ReaderOptions);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new InvalidDataException("The OpenCode configuration must contain a JSON object.");
        }

        var start = (int)reader.TokenStartIndex;
        reader.Skip();
        return ReadObject(start, (int)reader.BytesConsumed);
    }

    private JsonObjectSpan ReadObject(int start, int end)
    {
        var reader = new Utf8JsonReader(_utf8.AsSpan(start, end - start), ReaderOptions);
        reader.Read();
        var properties = new List<JsonPropertySpan>();
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var nameStart = start + (int)reader.TokenStartIndex;
            var name = reader.GetString()!;
            reader.Read();
            var valueStart = start + (int)reader.TokenStartIndex;
            reader.Skip();
            properties.Add(new JsonPropertySpan(name, nameStart, FindStringEnd(nameStart), valueStart, start + (int)reader.BytesConsumed));
        }

        return new JsonObjectSpan(start, end, properties);
    }

    private int FindStringEnd(int quoteIndex)
    {
        var index = quoteIndex + 1;
        while (_utf8[index] != (byte)'"')
        {
            index += _utf8[index] == (byte)'\\' ? 2 : 1;
        }

        return index + 1;
    }

    private int FindNextSignificant(int index)
    {
        while (index < _utf8.Length)
        {
            var current = _utf8[index];
            if (current is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            {
                index++;
            }
            else if (current == (byte)'/' && index + 1 < _utf8.Length && _utf8[index + 1] == (byte)'/')
            {
                index = _utf8.AsSpan(index).IndexOf((byte)'\n') is var offset and >= 0 ? index + offset + 1 : _utf8.Length;
            }
            else if (current == (byte)'/' && index + 1 < _utf8.Length && _utf8[index + 1] == (byte)'*')
            {
                index = _utf8.AsSpan(index + 2).IndexOf("*/"u8) is var offset and >= 0 ? index + offset + 4 : _utf8.Length;
            }
            else
            {
                return index;
            }
        }

        return index;
    }

    private int SkipInlineWhitespace(int index)
    {
        while (index < _utf8.Length && _utf8[index] is (byte)' ' or (byte)'\t')
        {
            index++;
        }

        return index;
    }

    private int SkipLineEnding(int index)
    {
        if (index < _utf8.Length && _utf8[index] == (byte)'\r')
        {
            index++;
        }

        return index < _utf8.Length && _utf8[index] == (byte)'\n' ? index + 1 : index;
    }

    private int GetRemovalStart(int nameStart)
    {
        var lineStart = GetLineStart(nameStart);
        return _utf8.AsSpan(lineStart, nameStart - lineStart).Trim(" \t"u8).IsEmpty ? lineStart : nameStart;
    }

    private int GetLineStart(int index)
    {
        var newline = _utf8.AsSpan(0, index).LastIndexOf((byte)'\n');
        return newline + 1;
    }

    private string GetLineIndent(int index)
    {
        var lineStart = GetLineStart(index);
        var indentEnd = SkipInlineWhitespace(lineStart);
        return Encoding.UTF8.GetString(_utf8, lineStart, Math.Min(indentEnd, index) - lineStart);
    }

    private string DetectIndentUnit()
    {
        try
        {
            var root = ReadRoot();
            if (root.Properties.Count > 0)
            {
                var indent = GetLineIndent(root.Properties[0].NameStart);
                if (indent.Length > 0 && GetLineStart(root.Properties[0].NameStart) > root.Start)
                {
                    return indent;
                }
            }
        }
        catch (JsonException)
        {
        }
        catch (InvalidDataException)
        {
        }

        return "  ";
    }

    private string Serialize(JsonNode value, string baseIndent)
    {
        var options = new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = true,
            IndentCharacter = _indentUnit[0],
            IndentSize = _indentUnit.Length
        };
        var lines = value.ToJsonString(options).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        return string.Join(_lineEnding, lines.Select((line, index) => index == 0 ? line : baseIndent + line));
    }

    private static string Quote(string name) =>
        JsonValue.Create(name).ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private void Splice(int start, int end, string replacement)
    {
        var replacementBytes = Encoding.UTF8.GetBytes(replacement);
        var updated = new byte[_utf8.Length - (end - start) + replacementBytes.Length];
        _utf8.AsSpan(0, start).CopyTo(updated);
        replacementBytes.CopyTo(updated.AsSpan(start));
        _utf8.AsSpan(end).CopyTo(updated.AsSpan(start + replacementBytes.Length));
        _utf8 = updated;
        IsChanged = true;
    }

    private sealed record JsonPropertySpan(string Name, int NameStart, int NameEnd, int ValueStart, int ValueEnd);

    private sealed record JsonObjectSpan(int Start, int End, List<JsonPropertySpan> Properties)
    {
        public JsonPropertySpan? Find(string name) =>
            Properties.LastOrDefault(property => string.Equals(property.Name, name, StringComparison.Ordinal));
    }
}
