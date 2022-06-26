using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net;

namespace OpenApiLinker
{
    public class LinkerGet
    {
        private readonly ILogger _logger;
        private readonly HttpClient _http;
        private readonly IMemoryCache _cache;

        public LinkerGet(ILoggerFactory loggerFactory, IHttpClientFactory httpFactory, IMemoryCache cache)
        {
            _logger = loggerFactory.CreateLogger<LinkerGet>();
            _http = httpFactory.CreateClient();
            _cache = cache;
        }

        [Function(nameof(LinkerGet))]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "linker")] HttpRequestData req,
            string openApiUrl)
        {
            try
            {
                // A collection of log entries to include in the linked spec.
                var info = new List<string>();
                
                // A cache of JObjects for use in this invocation
                var jObjectCache = new Dictionary<string, JObject>();

                AddInfo(info, $"This spec linked by DanielLarsenNZ/OpenApiLinker {DateTimeOffset.UtcNow}");
                AddInfo(info, "License: https://github.com/DanielLarsenNZ/OpenApiLinker/LICENSE");
                AddInfo(info, "THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.");

                AddInfo(info, $"Original spec URL (openApiUrl) = {openApiUrl}");

                if (string.IsNullOrWhiteSpace(openApiUrl))
                    throw new ArgumentException("Query parameter openApiUrl is required.");

                JObject json = await GetJObjectFromUri(jObjectCache, new Uri(openApiUrl));

                //var definitions = JObject.Parse("{}");
                var schemas = JObject.Parse("{}");


                var refs = json.Descendants().Where(d => d.Path.EndsWith(".schema.$ref"));
                foreach (var @ref in refs.Distinct())
                {
                    _logger.LogTrace($"@ref = {@ref}");

                    if (!@ref.HasValues) continue;

                    var values = @ref.Values<string>();
                    foreach (var refValue in values)
                    {
                        if (refValue != null && refValue.StartsWith("https://"))
                        {
                            _logger.LogTrace($"$ref: {refValue}");

                            var schemaRefUri = new Uri(refValue);

                            // Get definition
                            (string schemaName, JObject jsonSchema, JToken? schema) = await GetSchemaFromUri(jObjectCache, schemaRefUri);

                            if (schema is null)
                            {
                                AddInfoError(info, $"Schema {schemaName} was not found in JSON Schema {schemaRefUri}.");
                                continue;
                            }


                            // Add definition to definitions if not already exists
                            if (schemas[schemaName] == null)
                            {
                                _logger.LogInformation($"Adding schema {schemaName} to schemas");
                                schemas.Add(new JProperty(schemaName, schemas));
                            }

                            RewriteRef(@ref, schemaName);

                            // Get refs in that schema
                            JObject? schemasJObject = schema as JObject;
                            var subRefs = schemasJObject.Descendants().Where(d => d.Path.EndsWith("$ref"));

                            foreach (var subRef in subRefs.Distinct())
                            {
                                _logger.LogTrace($"@subRef = {subRef}");

                                if (!subRef.HasValues) continue;

                                //TODO: Single value always?
                                var subRefValues = subRef.Values<string>();
                                foreach (var subRefValue in subRefValues)
                                {
                                    if (subRefValue is null) continue;

                                    if (subRefValue.StartsWith("https://"))
                                    {
                                        //TODO Support nested refs?
                                        throw new NotSupportedException();
                                    }

                                    _logger.LogTrace($"$ref: {subRefValue}");

                                    // Get definition
                                    string subRefDefinitionName = DefinitionNameFromFragment(subRefValue);
                                    var subRefDefinition = jsonSchema["definitions"]?[subRefDefinitionName];

                                    if (subRefDefinition is null)
                                    {
                                        AddInfoError(info, $"Definition {subRefDefinitionName} was not found in JSON Schema {schemaRefUri}.");
                                        continue;
                                    }

                                    // Add definition to definitions if not already exists
                                    if (schemas[subRefDefinitionName] == null)
                                    {
                                        _logger.LogInformation($"Adding schema {subRefDefinitionName} to schemas");
                                        schemas.Add(new JProperty(subRefDefinitionName, subRefDefinition));
                                    }

                                    // Rewrite definition ref to component ref
                                    RewriteRef(subRef, subRefDefinitionName);
                                }
                            }
                        }
                    }
                }

                JObject? componentsJObject = json["components"] as JObject;

                if (componentsJObject is null) throw new NotSupportedException("No components found in open api json");

                AddJObjectProperty(componentsJObject, "schemas", schemas);

                // Add info

                string infoJson = JsonConvert.SerializeObject(info.ToArray());
                JObject infoJObject = JObject.Parse($" {{ \"x-openapilinker-info\":[{infoJson}] }} ");
                AddJObjectProperty(json, "info", infoJObject);

                //TODO: Stream response
                var response = req.CreateResponse(HttpStatusCode.OK);
                response.Headers.Add("Content-Type", "application/json");
                await response.WriteStringAsync(json.ToString());

                return response;
            }
            catch (ArgumentException ex)
            {
                _logger.LogError(ex, ex.Message);
                return BadRequestResponse(req, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, ex.Message);
                var response = req.CreateResponse(HttpStatusCode.InternalServerError);
                response.Headers.Add("Content-Type", "text/plain");
                await response.WriteStringAsync(ex.Message);
                return response;
            }
        }

        private static void RewriteRef(JToken @ref, string schemaName)
        {
            // Rewrite definition ref to component ref
            JObject? refJObject = @ref.Parent as JObject;
            if (refJObject is null) throw new InvalidOperationException();
            refJObject["$ref"] = $"#/components/schemas/{schemaName}";
        }

        /// <summary>
        /// Add a JObject as a property of another Jobject. If the property already exists, the content will be appended.
        /// </summary>
        private void AddJObjectProperty(JObject json, string propertyName, JObject jObjectToAdd)
        {
            if (json[propertyName] is null)
            {
                json.Add(new JProperty(propertyName, jObjectToAdd));
                return;
            }

            foreach (var child in jObjectToAdd.Children())
            {

                if (json[propertyName]?[child.Path] is null)
                {
                    json.Property(propertyName).AddAfterSelf(new JProperty(child.Path, child.Values().FirstOrDefault()));
                }
                else
                {
                    _logger.LogWarning($"{child.Path} already exists on {json.Path}{propertyName}");
                }
            }
        }

        private async Task<(string, JObject, JToken?)> GetSchemaFromUri(Dictionary<string, JObject> jObjectCache, Uri uri)
        {
            //TODO: Check nulls
            JObject refJson = await GetJObjectFromUri(jObjectCache, uri);
            string definitionPath = uri.Fragment.Replace("#/", string.Empty);
            string definitionName = DefinitionNameFromFragment(definitionPath);
            return (definitionName, refJson, refJson["definitions"]?[definitionName]);
        }

        private static string DefinitionNameFromFragment(string fragment) => fragment.Split("/").Last();


        private async Task<JObject> GetJObjectFromUri(Dictionary<string, JObject> jObjectCache, Uri uri)
        {
            string key = $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}";

            // This cache is per invocation
            if (jObjectCache.ContainsKey(key)) return jObjectCache[key];

            // This cache is for the lifetime of the Function App (worker)
            if (_cache.TryGetValue(key, out JObject json))
            {
                _logger.LogInformation($"Cache hit {key}");
                // Return a copy of the cached JObject as Memory Cache objects are by ref
                return CopyAndCacheJObject(jObjectCache, key, json);
            }

            _logger.LogInformation($"Cache miss {key}");

            using (var stream = await _http.GetStreamAsync(uri))
            using (StreamReader sr = new StreamReader(stream))
            using (JsonReader reader = new JsonTextReader(sr))
            {
                json = await JObject.LoadAsync(reader);
            }

            _cache.Set(key, json);

            // Return a copy of the cached JObject as Memory Cache objects are by ref
            return CopyAndCacheJObject(jObjectCache, key, json);
        }

        private JObject CopyAndCacheJObject(Dictionary<string, JObject> jObjectCache, string key, JObject json)
        {
            var copyOfJson = new JObject(json);
            jObjectCache[key] = copyOfJson;
            return copyOfJson;
        }

        private HttpResponseData BadRequestResponse(HttpRequestData req, string bodyMessage)
        {
            var response = req.CreateResponse(HttpStatusCode.BadRequest);
            response.WriteString(bodyMessage);
            return response;
        }

        private void AddInfo(List<string> info, string message)
        {
            info.Add(message);
            _logger.LogInformation(message);
        }

        private void AddInfoError(List<string> info, string message)
        {
            info.Add($"ERROR: {message}");
            _logger.LogWarning(message);
        }
    }
}
