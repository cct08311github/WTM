#nullable enable
// WF-4: Canonical / deterministic JSON serializer for WorkflowGraph documents.
// WF-21.1: Additive Canonicalize(string) seam for the raw-bytes designer path.
//
// Determinism contract (repo prompt-cache-stability discipline):
//   • Object properties are emitted in sorted alphabetical order by JSON property name.
//   • Arrays are preserved in their authored order (ordering arrays would change semantics).
//   • No indentation — compact single-line output.
//   • Enum values written as strings (JsonStringEnumConverter).
//   • Null properties suppressed (WhenWritingNull) so absent optional fields do not affect
//     the hash.
//   • Two WorkflowGraph instances that are logically identical but constructed with
//     different in-memory property-assignment order produce byte-identical JSON.
//
// Algorithm: manual property-sorted serialization via Utf8JsonWriter rather than
// reflection, to guarantee stable output regardless of STJ version or runtime
// property-enumeration order changes.  Uses a recursive JsonDocument / JsonElement
// intermediary so we can sort object keys at every depth.
//
// This file exposes three public members:
//   WorkflowGraphSerializer.Serialize(WorkflowGraph)   → canonical JSON string (typed path)
//   WorkflowGraphSerializer.Deserialize(string)         → WorkflowGraph
//   WorkflowGraphSerializer.Canonicalize(string json)  → canonical JSON string (raw path, WF-21.1)
//
// Fidelity contract for Canonicalize:
//   • Unknown fields survive: the raw-document walk does not go through the typed model.
//   • Exact number literals survive: numbers are emitted via WriteRawValue(GetRawText()).
//   • String escape forms are normalized (WriteStringValue re-encodes) — equal values hash
//     equally, which is the correct semantic (the typed path already normalizes this way).
//   • Canonicalize(Serialize(graph)) == Serialize(graph) for any known-field-only document.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WalkingTec.Mvvm.WorkFlow.Definition;

/// <summary>
/// Canonical, deterministic JSON serializer for <see cref="WorkflowGraph"/> documents.
///
/// <para><strong>Determinism guarantee:</strong> for any two <see cref="WorkflowGraph"/>
/// instances that represent the same logical graph, <see cref="Serialize"/> produces
/// byte-identical output regardless of the in-memory property-assignment order or
/// dictionary/list construction order.  This is required for the SHA-256
/// <see cref="Models.ProcessDefinitionVersion.ContentHash"/> to be stable across
/// serialization round-trips.</para>
///
/// <para><strong>Technique:</strong>
/// <list type="number">
///   <item>Serialize to an intermediate <see cref="JsonDocument"/> using the base
///   STJ options (enums as strings, WhenWritingNull, no indentation).</item>
///   <item>Walk the document recursively, writing to a <see cref="Utf8JsonWriter"/>
///   with object keys sorted alphabetically at every level.</item>
///   <item>Return the resulting UTF-8 bytes decoded as a string.</item>
/// </list>
/// Arrays are preserved in their authored order — reordering arrays would change
/// the semantic meaning of the graph (e.g. branch evaluation order in Condition nodes).
/// </para>
/// </summary>
public static class WorkflowGraphSerializer
{
    // ── Base STJ options (used for initial round-trip; NOT for canonical output) ──

    private static readonly JsonSerializerOptions _baseOptions = new()
    {
        PropertyNamingPolicy = null,                  // respect JsonPropertyName attributes
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Serialize <paramref name="graph"/> to canonical (deterministic, key-sorted,
    /// compact) JSON.
    /// </summary>
    /// <param name="graph">The graph to serialize.  Must not be null.</param>
    /// <returns>Canonical JSON string, UTF-8 safe.</returns>
    public static string Serialize(WorkflowGraph graph)
    {
        if (graph is null) throw new ArgumentNullException(nameof(graph));

        // Step 1: serialize to intermediate JSON via base STJ options.
        // This handles all the JsonPropertyName / WhenWritingNull / enum-as-string
        // concerns correctly; the intermediate form may have unsorted keys.
        var intermediate = JsonSerializer.Serialize(graph, _baseOptions);

        // Step 2: parse into a JsonDocument and write back with sorted keys.
        using var doc = JsonDocument.Parse(intermediate);
        return WriteCanonical(doc.RootElement);
    }

    /// <summary>
    /// Deserialize a canonical JSON string back to a <see cref="WorkflowGraph"/>.
    /// </summary>
    /// <param name="json">Canonical JSON produced by <see cref="Serialize"/>.</param>
    /// <returns>Deserialized graph.</returns>
    /// <exception cref="JsonException">When <paramref name="json"/> is malformed.</exception>
    public static WorkflowGraph Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("GraphJson must not be null or empty.", nameof(json));

        return JsonSerializer.Deserialize<WorkflowGraph>(json, _baseOptions)
               ?? throw new JsonException("Deserialized WorkflowGraph was null.");
    }

    // ── WF-21.1: Canonicalize seam (raw-bytes designer path) ─────────────────

    /// <summary>
    /// Canonicalize an arbitrary JSON string: sort object keys at every depth,
    /// preserve exact number literals and unknown fields, normalize string escape forms.
    /// This is the fidelity keystone for the raw-bytes designer path (§2.2 spec).
    ///
    /// <para><strong>Fidelity contract:</strong>
    /// <list type="bullet">
    ///   <item>Unknown fields (not in the typed schema) survive unchanged.</item>
    ///   <item>Number literals are preserved verbatim (e.g. <c>0.50</c>, <c>1E2</c>,
    ///         <c>9007199254740993</c>).</item>
    ///   <item>String escape forms are normalized — equal values hash equally.</item>
    ///   <item>For documents containing only known fields:
    ///         <c>Canonicalize(raw) == Serialize(Deserialize(raw))</c>.</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="rawJson">
    /// A valid JSON object string.  Must not be null or empty.
    /// </param>
    /// <returns>Canonical (key-sorted, compact) JSON string.</returns>
    /// <exception cref="ArgumentException">When <paramref name="rawJson"/> is null or whitespace.</exception>
    /// <exception cref="JsonException">When <paramref name="rawJson"/> is not valid JSON, or when it contains duplicate JSON property names.</exception>
    public static string Canonicalize(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            throw new ArgumentException("rawJson must not be null or empty.", nameof(rawJson));

        // Parse — validates well-formedness, exact number literal fidelity, and
        // rejects duplicate property names (AllowDuplicateProperties=false).
        // Duplicate keys are ambiguous and non-canonical: two JSON objects that differ only
        // in which duplicate copy "wins" would otherwise produce the same hash, enabling
        // malicious or buggy graphs to carry shadowed keys that survive round-trips.
        // A JsonException is thrown on parse if duplicates are detected; callers
        // (PublishRawAsync, ValidateRaw) map it to ValidationFailed / BadRequest.
        using var doc = JsonDocument.Parse(rawJson, new JsonDocumentOptions
        {
            AllowDuplicateProperties = false,
        });
        return WriteCanonical(doc.RootElement);
    }

    // ── Canonical writer ───────────────────────────────────────────────────────

    /// <summary>
    /// Recursively write a <see cref="JsonElement"/> with object keys sorted
    /// alphabetically (ordinal) at every level.  Arrays are preserved as-is.
    /// </summary>
    private static string WriteCanonical(JsonElement root)
    {
        var buf = new ArrayBufferWriter<byte>(512);
        using var writer = new Utf8JsonWriter(buf, new JsonWriterOptions
        {
            Indented = false,
            SkipValidation = false,
        });

        WriteElement(writer, root);
        writer.Flush();

        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    private static void WriteElement(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                // Sort properties alphabetically by JSON property name (ordinal).
                var props = element.EnumerateObject()
                    .OrderBy(p => p.Name, StringComparer.Ordinal)
                    .ToList();
                foreach (var prop in props)
                {
                    writer.WritePropertyName(prop.Name);
                    WriteElement(writer, prop.Value);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteElement(writer, item);
                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;

            case JsonValueKind.Number:
                // Preserve exact numeric representation.
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: false);
                break;

            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;

            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;

            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;

            default:
                // Undefined / other — write raw to avoid silent data loss.
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: false);
                break;
        }
    }
}
