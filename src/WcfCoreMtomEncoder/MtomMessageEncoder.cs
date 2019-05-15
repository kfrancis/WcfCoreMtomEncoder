using Microsoft.Extensions.Primitives;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.ServiceModel.Channels;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace WcfCoreMtomEncoder
{
    public class MtomMessageEncoder : MessageEncoder
    {
        private readonly MessageEncoder _innerEncoder;

        public MtomMessageEncoder(MessageEncoder innerEncoder)
        {
            _innerEncoder = innerEncoder;
        }

        public override string ContentType => _innerEncoder.ContentType;
        public override string MediaType => _innerEncoder.MediaType;
        public override MessageVersion MessageVersion => _innerEncoder.MessageVersion;

        public override Message ReadMessage(ArraySegment<byte> buffer, BufferManager bufferManager, string contentType)
        {
            using (var stream = new MemoryStream(buffer.ToArray()))
            {
                var message = ReadMessage(stream, 1024, contentType);
                bufferManager.ReturnBuffer(buffer.Array);
                return message;
            }
        }
        
        public override Message ReadMessage(Stream stream, int maxSizeOfHeaders, string contentType)
        {
            var parts = (
                from p in GetMultipartContent(stream, contentType)
                select new MtomPart(p)).ToList();

            var mainPart = (
                from part in parts
                where part.ContentId == new ContentType(contentType).Parameters?["start"]
                select part).SingleOrDefault() ?? parts.First();

            var mainContent = ResolveRefs(mainPart.GetStringContentForEncoder(_innerEncoder), parts);
            var mainContentStream = CreateStream(mainContent, mainPart.ContentType);

            return _innerEncoder.ReadMessage(mainContentStream, maxSizeOfHeaders, mainPart.ContentType.ToString());
        }

        public override ArraySegment<byte> WriteMessage(Message message, int maxMessageSize, BufferManager bufferManager, int messageOffset)
        {
            return _innerEncoder.WriteMessage(message, maxMessageSize, bufferManager, messageOffset);
        }

        public override void WriteMessage(Message message, Stream stream)
        {
            _innerEncoder.WriteMessage(message, stream);
        }

        public override bool IsContentTypeSupported(string contentType)
        {
            if (_innerEncoder.IsContentTypeSupported(contentType))
                return true;

            var contentTypes = contentType.Split(';').Select(c => c.Trim()).ToList();
            
            if (contentTypes.Contains("multipart/related", StringComparer.OrdinalIgnoreCase) &&
                contentTypes.Contains("type=\"application/xop+xml\"", StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
            return false;
        }

        public override T GetProperty<T>()
        {
            return _innerEncoder.GetProperty<T>();
        }

        public static bool IsQuoted(StringSegment input)
        {
            return !StringSegment.IsNullOrEmpty(input) && input.Length >= 2 && input[0] == '"' && input[input.Length - 1] == '"';
        }

        public static StringSegment RemoveQuotes(StringSegment input)
        {
            if (IsQuoted(input))
            {
                input = input.Subsegment(1, input.Length - 2);
            }
            return input;
        }

        // Content-Type: multipart/form-data; boundary="----WebKitFormBoundarymx2fSWqWSd0OxQqq"
        // The spec says 70 characters is a reasonable limit.
        private static string GetBoundary(MediaTypeHeaderValue contentType, int lengthLimit)
        {
            var boundaryHeader = contentType.Parameters.FirstOrDefault(f => f.Name == "boundary");
            var boundary = RemoveQuotes(boundaryHeader.Value);
            if (StringSegment.IsNullOrEmpty(boundary))
            {
                throw new InvalidDataException("Missing content-type boundary.");
            }
            if (boundary.Length > lengthLimit)
            {
                throw new InvalidDataException($"Multipart boundary length limit {lengthLimit} exceeded.");
            }
            return boundary.ToString();
        }

        private static IEnumerable<HttpContent> GetMultipartContent(Stream stream, string contentType)
        {
            var content = new StreamContent(stream);

            content.Headers.Add("Content-Type", contentType);

            var formAccumulator = new KeyValueAccumulator();
            var boundary = GetBoundary(content.Headers.ContentType, 70);

            var multipartReader = new MultipartReader(boundary, stream)
            {

            };

            var section = multipartReader.ReadNextSectionAsync().Result;
            while (section != null)
            {
                // Parse the content disposition here and pass it further to avoid reparsings
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var contentDisposition))
                {
                    throw new InvalidDataException("Form section has invalid Content-Disposition value: " + section.ContentDisposition);
                }

                if (contentDisposition.IsFileDisposition())
                {
                    var fileSection = new FileMultipartSection(section, contentDisposition);

                    // Enable buffering for the file if not already done for the full body
                    section.EnableRewind(
                        _request.HttpContext.Response.RegisterForDispose,
                        _options.MemoryBufferThreshold, _options.MultipartBodyLengthLimit);

                    // Find the end
                    await section.Body.DrainAsync(cancellationToken);

                    var name = fileSection.Name;
                    var fileName = fileSection.FileName;

                    FormFile file;
                    if (section.BaseStreamOffset.HasValue)
                    {
                        // Relative reference to buffered request body
                        file = new FormFile(_request.Body, section.BaseStreamOffset.GetValueOrDefault(), section.Body.Length, name, fileName);
                    }
                    else
                    {
                        // Individually buffered file body
                        file = new FormFile(section.Body, 0, section.Body.Length, name, fileName);
                    }
                    file.Headers = new HeaderDictionary(section.Headers);

                    if (files == null)
                    {
                        files = new FormFileCollection();
                    }
                    if (files.Count >= _options.ValueCountLimit)
                    {
                        throw new InvalidDataException($"Form value count limit {_options.ValueCountLimit} exceeded.");
                    }
                    files.Add(file);
                }
                else if (contentDisposition.IsFormDisposition())
                {
                    var formDataSection = new FormMultipartSection(section, contentDisposition);

                    // Content-Disposition: form-data; name="key"
                    //
                    // value

                    // Do not limit the key name length here because the multipart headers length limit is already in effect.
                    var key = formDataSection.Name;
                    var value = await formDataSection.GetValueAsync();

                    formAccumulator.Append(key, value);
                    if (formAccumulator.ValueCount > _options.ValueCountLimit)
                    {
                        throw new InvalidDataException($"Form value count limit {_options.ValueCountLimit} exceeded.");
                    }
                }
                else
                {
                    System.Diagnostics.Debug.Assert(false, "Unrecognized content-disposition for this section: " + section.ContentDisposition);
                }

                section = multipartReader.ReadNextSectionAsync().Result;
            }

            return content.ReadAsMultipartAsync().Result.Contents;
        }

        private static string ResolveRefs(string mainContent, IList<MtomPart> parts)
        {
            bool ReferenceMatch(XAttribute hrefAttr, MtomPart part)
            {
                var partId = Regex.Match(part.ContentId, "<(?<uri>.*)>");
                var href = Regex.Match(hrefAttr.Value, "cid:(?<uri>.*)");

                return href.Groups["uri"].Value == partId.Groups["uri"].Value;
            }

            var doc = XDocument.Parse(mainContent);
            var references = doc.Descendants(XName.Get("Include", "http://www.w3.org/2004/08/xop/include")).ToList();

            foreach (var reference in references)
            {
                var referencedPart = (
                    from part in parts
                    where ReferenceMatch(reference.Attribute("href"), part)
                    select part).Single();

                reference.ReplaceWith(Convert.ToBase64String(referencedPart.GetRawContent()));
            }
            return doc.ToString(SaveOptions.DisableFormatting);
        }

        private static Stream CreateStream(string content, MediaTypeHeaderValue contentType)
        {
            var encoding = !string.IsNullOrEmpty(contentType.CharSet)
                ? Encoding.GetEncoding(contentType.CharSet)
                : Encoding.Default;

            return new MemoryStream(encoding.GetBytes(content));
        }
    }
}