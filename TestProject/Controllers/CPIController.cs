using Microsoft.AspNetCore.Mvc;
using System.Net.Http.Headers;
using System.Security.Cryptography.Xml;
using System;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using System.Net;
using static System.Net.Mime.MediaTypeNames;
using static System.Runtime.InteropServices.JavaScript.JSType;
using System.Globalization;
using Microsoft.Extensions.Primitives;

namespace TestProject.Controllers
{
    public class CPIController : Controller
    {
        private const string URL = "https://api.bls.gov/publicAPI/v1/timeseries/data/LAUCN040010000000005";
        IHttpClientFactory _clientFactory;

        private readonly IMemoryCache _memoryCache;
        private readonly ILogger _logger;
      

        public CPIController(IMemoryCache memoryCache, IHttpClientFactory clientFactory, ILogger<CPIController> logger)
        {
            _memoryCache = memoryCache;
            _clientFactory = clientFactory;
            _logger = logger;
        }


        [HttpGet("GetCPI")]
        public async Task<IActionResult> GetCPI(int year, string month)
        {
            // Validate the month and year inputs
            if (!ValidateMonthYear(month, year, out string message))
            {
                return BadRequest(message);
            }

            var cacheKey = $"CPI_{year}_{month}";

            // Check if the data is cached
            if (!_memoryCache.TryGetValue(cacheKey, out CPIData cpiData))
            {
                var client = _clientFactory.CreateClient();
                var url = $"{URL}?startYear={year}&endYear={year}"; // Adjust URL as needed

                // Fetch data from the external API
                var response = await client.GetAsync(url);

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();

                    try
                    {
                        using JsonDocument doc = JsonDocument.Parse(content);
                        var root = doc.RootElement;

                        if (root.TryGetProperty("Results", out var resultsElement) &&
                            resultsElement.TryGetProperty("series", out var seriesElement) &&
                            seriesElement.GetArrayLength() > 0)
                        {
                            var series = seriesElement[0];
                            if (series.TryGetProperty("data", out var dataElement))
                            {
                                // Find the relevant CPI entry for the specified month and year

                                var cpiEntry = dataElement.EnumerateArray()
                                    .FirstOrDefault(d => d.GetProperty("year").GetString() == year.ToString() &&
                                    string.Equals(d.GetProperty("periodName").GetString(), month, StringComparison.OrdinalIgnoreCase));


                                if (cpiEntry.ValueKind != JsonValueKind.Undefined)
                                {
                                    var value = int.Parse(cpiEntry.GetProperty("value").GetString());
                                    string[] footnotes = ParseFootnotes(cpiEntry, _logger);

                                    cpiData = new CPIData
                                    {
                                        Value = value,
                                        Notes = new FootNotes(footnotes)
                                    };

                                    // Cache the data for future requests
                                    var cacheEntryOptions = new MemoryCacheEntryOptions()
                                        .SetAbsoluteExpiration(TimeSpan.FromHours(24));

                                    _memoryCache.Set(cacheKey, cpiData, cacheEntryOptions);
                                    return Ok(cpiData);
                                }
                                else
                                {
                                    // Handle case where no CPI data is found
                                    string[] footNotes = { $"CPI data not found for {month} {year}" };
                                    cpiData = new CPIData { Value = null, Notes = new FootNotes(footNotes) };
                                    _logger.LogWarning("CPI data not found for {Month} {Year}", month, year);
                                    return NotFound(cpiData);
                                }
                            }
                            else
                            {
                                // Handle case where no data is found in the series
                                string[] footNotes = { "No data found in the series" };
                                cpiData = new CPIData { Value = null, Notes = new FootNotes(footNotes) };
                                _logger.LogWarning("No data found in the series");
                                return NotFound(cpiData);
                            }
                        }
                        else
                        {
                            // Handle invalid or empty response
                            _logger.LogWarning("Invalid or empty response from BLS API.");
                            return BadRequest("Invalid or empty response from BLS API.");
                        }
                    }
                    catch (JsonException ex)
                    {
                        // Handle JSON parsing errors
                        _logger.LogError(ex, "Error parsing JSON response");
                        return BadRequest("Error parsing API response");
                    }
                }
                else
                {
                    // Handle HTTP response errors
                    _logger.LogError("Error response from BLS API. Status code: {StatusCode}", response.StatusCode);
                    return StatusCode((int)response.StatusCode);
                }
            }

            // If cached data is found, return it
            return Ok(cpiData);
        }


        private bool ValidateMonthYear(string month, int year, out string message)
        {
            message = string.Empty;
            bool valid = true;

            if (!DateTime.TryParseExact(month, "MMMM", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            {
                message = "Invalid month name.";
                valid = false;
            }

            if (year.ToString().Length == 4 && year < 1000 || year > 9999)
            {
                message = message + " Invalid Year. It must be a 4-digit number between 1000 and 9999.";
                valid = false;
            }
            return valid;
        }

        private static string[] ParseFootnotes(JsonElement cpiEntry, ILogger logger)
        {
            try
            {
                if (cpiEntry.TryGetProperty("footnotes", out var footnotesElement))
                {
                    if (footnotesElement.ValueKind == JsonValueKind.Array)
                    {
                        return footnotesElement.EnumerateArray()
                            .Select(f => f.TryGetProperty("text", out var textElement) ? textElement.GetString() : null)
                            .Where(text => !string.IsNullOrEmpty(text))
                            .ToArray();
                    }
                    else
                    {
                        logger.LogWarning("Footnotes property is not an array. Type: {FootnotesType}", footnotesElement.ValueKind);
                        return Array.Empty<string>();
                    }
                }
                else
                {
                    logger.LogWarning("Footnotes property not found in CPI entry");
                    return Array.Empty<string>();
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error parsing footnotes");
                return Array.Empty<string>();
            }
        }
    }
}
