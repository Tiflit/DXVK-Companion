using System;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json;
using DXVKCompanion.Models;

namespace DXVKCompanion.Storage
{
    internal enum SchemaCompatibilityStatus
    {
        Missing = 0,
        Compatible = 1,
        IncompatibleNewer = 2,
        Malformed = 3
    }

    internal sealed class SchemaAssessment
    {
        public SchemaCompatibilityStatus Status { get; }
        public string? FoundSchemaVersionText { get; }
        public int SupportedSchemaVersion => GameLibrary.CurrentSchemaVersion;

        public bool IsRefused => Status == SchemaCompatibilityStatus.IncompatibleNewer;
        public bool IsCompatible => Status == SchemaCompatibilityStatus.Compatible || Status == SchemaCompatibilityStatus.Missing;

        public SchemaAssessment(SchemaCompatibilityStatus status, string? foundSchemaVersionText = null)
        {
            Status = status;
            FoundSchemaVersionText = foundSchemaVersionText;
        }

        public static SchemaAssessment Missing() => new(SchemaCompatibilityStatus.Missing);
        public static SchemaAssessment Compatible(string? versionText = null) => new(SchemaCompatibilityStatus.Compatible, versionText);
        public static SchemaAssessment IncompatibleNewer(string foundVersionText) => new(SchemaCompatibilityStatus.IncompatibleNewer, foundVersionText);
        public static SchemaAssessment Malformed() => new(SchemaCompatibilityStatus.Malformed);
    }

    internal sealed class IncompatibleLibrarySchemaException : InvalidOperationException
    {
        public string? FoundSchemaVersionText { get; }
        public int SupportedSchemaVersion { get; }

        public IncompatibleLibrarySchemaException(string? foundSchemaVersionText, int supportedSchemaVersion, string message)
            : base(message)
        {
            FoundSchemaVersionText = foundSchemaVersionText;
            SupportedSchemaVersion = supportedSchemaVersion;
        }
    }

    internal static class GameLibrarySchemaCompatibility
    {
        public static SchemaAssessment AssessFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return SchemaAssessment.Missing();

            string text;
            try
            {
                text = File.ReadAllText(filePath);
            }
            catch
            {
                return SchemaAssessment.Malformed();
            }

            if (string.IsNullOrWhiteSpace(text))
                return SchemaAssessment.Malformed();

            return AssessJson(text);
        }

        public static SchemaAssessment AssessJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return SchemaAssessment.Malformed();

            if (json.Length > 0 && json[0] == '\uFEFF')
            {
                json = json.Substring(1);
            }

            try
            {
                using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Disallow,
                    AllowTrailingCommas = false
                });

                return AssessJsonDocument(doc);
            }
            catch (JsonException)
            {
                return SchemaAssessment.Malformed();
            }
            catch (Exception)
            {
                return SchemaAssessment.Malformed();
            }
        }

        public static SchemaAssessment AssessJson(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return SchemaAssessment.Malformed();

            try
            {
                using var ms = new MemoryStream(bytes);
                using var reader = new StreamReader(ms, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                string text = reader.ReadToEnd();
                return AssessJson(text);
            }
            catch
            {
                return SchemaAssessment.Malformed();
            }
        }

        private static SchemaAssessment AssessJsonDocument(JsonDocument doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return SchemaAssessment.Malformed();

            JsonProperty? schemaProp = null;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(prop.Name, nameof(GameLibrary.SchemaVersion), StringComparison.OrdinalIgnoreCase))
                {
                    // In System.Text.Json, typed deserialization applies the last matching property.
                    // Match effective property handling by tracking the last occurrence.
                    schemaProp = prop;
                }
            }

            if (schemaProp == null)
            {
                // Property missing from root object: not newer, compatible default
                return SchemaAssessment.Compatible();
            }

            var valueElement = schemaProp.Value.Value;
            if (valueElement.ValueKind != JsonValueKind.Number)
            {
                // Non-integer, string, null, bool, array, object: not newer, malformed marker
                return SchemaAssessment.Malformed();
            }

            string rawText = valueElement.GetRawText().Trim();

            // Must be an integer: reject decimals and scientific exponents
            if (rawText.Contains('.') || rawText.Contains('e') || rawText.Contains('E'))
            {
                return SchemaAssessment.Malformed();
            }

            // Reject negative signs
            if (rawText.StartsWith("-", StringComparison.Ordinal))
            {
                return SchemaAssessment.Malformed();
            }

            if (!BigInteger.TryParse(rawText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version) || version <= 0)
            {
                // Zero, negative, or unparseable: not newer, malformed marker
                return SchemaAssessment.Malformed();
            }

            if (version > GameLibrary.CurrentSchemaVersion)
            {
                return SchemaAssessment.IncompatibleNewer(rawText);
            }

            return SchemaAssessment.Compatible(rawText);
        }
    }
}
