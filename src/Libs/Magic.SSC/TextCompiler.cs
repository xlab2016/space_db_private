using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Magic.Kernel.Devices;
using Magic.Kernel.Devices.SSC;
using Magic.Kernel.Space;

namespace Magic.SSC
{
    /// <summary>
    /// Compiles plain text into hierarchical structure: paragraphs and sentences as vertices.
    /// Vertices are ordered: paragraphs first, then sentences; RelationOrdinals link paragraph → sentence by list index.
    /// </summary>
    public class TextCompiler : ISSCompiler
    {
        private static readonly Regex SentenceSplitRegex = new Regex(
            @"(?<=[.!?])\s+(?=[^\s])|(?<=[.!?])$",
            RegexOptions.Compiled | RegexOptions.Multiline);

        /// <summary>Compiles text into vertices (paragraphs, sentences) and relation ordinals.</summary>
        public TextCompilationResult Compile(string content)
        {
            if (string.IsNullOrEmpty(content))
                return new TextCompilationResult();

            var vertices = new List<Vertex>();
            var relationOrdinals = new List<(int FromOrdinal, int ToOrdinal)>();

            var paragraphs = SplitParagraphs(content);

            for (int p = 0; p < paragraphs.Count; p++)
            {
                var paragraphText = paragraphs[p].Trim();
                if (string.IsNullOrEmpty(paragraphText))
                    continue;

                var paragraphVertex = CreateTextVertex(paragraphText, p, 0);
                int paragraphOrdinal = vertices.Count;
                vertices.Add(paragraphVertex);

                var sentences = SplitSentences(paragraphText);
                for (int s = 0; s < sentences.Count; s++)
                {
                    var sentenceText = sentences[s].Trim();
                    if (string.IsNullOrEmpty(sentenceText))
                        continue;

                    var sentenceVertex = CreateTextVertex(sentenceText, p, s + 1);
                    int sentenceOrdinal = vertices.Count;
                    vertices.Add(sentenceVertex);
                    relationOrdinals.Add((paragraphOrdinal, sentenceOrdinal));
                }
            }

            return new TextCompilationResult
            {
                Vertices = vertices,
                RelationOrdinals = relationOrdinals
            };
        }

        /// <inheritdoc />
        public async Task<SSCResult> CompileAsync(IStreamDevice device, ISpaceDisk disk)
        {
            if (device == null || disk == null)
                return new SSCResult { IsSuccess = false, ErrorMessage = "Device or disk is null." };

            var (result, chunk) = await device.ReadChunkAsync().ConfigureAwait(false);
            if (!result.IsSuccess || chunk?.Data == null || chunk.Data.Length == 0)
                return new SSCResult { IsSuccess = false, ErrorMessage = result.ErrorMessage ?? "No chunk data." };

            if (chunk.DataFormat != DataFormat.Text)
                throw new NotSupportedException($"Only DataFormat.Text is supported. Got: {chunk.DataFormat}.");

            var content = Encoding.UTF8.GetString(chunk.Data);
            var compiled = Compile(content);
            var sscResult = new SSCResult { IsSuccess = true };
            string? spaceName = null;

            try
            {
                var indices = new List<long>();
                foreach (var vertex in compiled.Vertices)
                {
                    var addResult = await disk.AddVertex(vertex, spaceName).ConfigureAwait(false);
                    if (addResult != SpaceOperationResult.Success)
                    {
                        sscResult.IsSuccess = false;
                        sscResult.ErrorMessage = $"AddVertex: {addResult}";
                        return sscResult;
                    }
                    indices.Add(vertex.Index!.Value);
                    sscResult.VertexIndices.Add(vertex.Index!.Value);
                }

                foreach (var (fromOrd, toOrd) in compiled.RelationOrdinals)
                {
                    var relation = new Relation
                    {
                        FromIndex = indices[fromOrd],
                        ToIndex = indices[toOrd],
                        FromType = EntityType.Vertex,
                        ToType = EntityType.Vertex
                    };
                    var addRelResult = await disk.AddRelation(relation, spaceName).ConfigureAwait(false);
                    if (addRelResult != SpaceOperationResult.Success)
                    {
                        sscResult.IsSuccess = false;
                        sscResult.ErrorMessage = $"AddRelation({relation.FromIndex}->{relation.ToIndex}): {addRelResult}";
                        return sscResult;
                    }
                    sscResult.RelationIndices.Add(relation.Index!.Value);
                }
            }
            catch (Exception ex)
            {
                sscResult.IsSuccess = false;
                sscResult.ErrorMessage = ex.Message;
            }

            return sscResult;
        }

        /// <summary>Split text into paragraphs (double newline).</summary>
        public static List<string> SplitParagraphs(string text)
        {
            if (string.IsNullOrEmpty(text))
                return new List<string>();
            var normalized = text.Replace("\r\n", "\n").Replace("\r", "\n");
            return normalized.Split(new[] { "\n\n" }, StringSplitOptions.None).ToList();
        }

        /// <summary>Split paragraph into sentences (boundaries: . ! ?).</summary>
        public static List<string> SplitSentences(string paragraph)
        {
            if (string.IsNullOrEmpty(paragraph))
                return new List<string>();
            var parts = SentenceSplitRegex.Split(paragraph);
            return parts.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        }

        private static Vertex CreateTextVertex(string text, int paragraphIndex, int sentenceIndex)
        {
            var hash = ComputeHash(text);
            return new Vertex
            {
                Name = hash,
                Position = new Position { Dimensions = new List<float> { paragraphIndex, sentenceIndex } },
                Data = new EntityData
                {
                    Type = new HierarchicalDataType { Types = new List<DataType> { DataType.Text } },
                    Data = ToBase64(text)
                }
            };
        }

        private static string ComputeHash(string content)
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            var hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static string ToBase64(string text)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        }
    }
}
