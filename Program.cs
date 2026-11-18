using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace MigrateToCodeberg
{
    class Program
    {
        private const string CONFIG_FILE = "config.json";

        static async Task Main(string[] args)
        {
            Console.WriteLine("");
            Console.WriteLine("    ----------------------------------------------");
            Console.WriteLine("    Welcome to Github to Codeberg Migration Script");
            Console.WriteLine("    ----------------------------------------------");
            Console.WriteLine("");

            Console.Write("    Remove push mirrors only (skip migration)? (y/n, default n): ");
            bool removeMirrorsOnly = Console.ReadLine()?.Trim().ToLower() == "y";

            Config config = LoadConfig();

            if (config == null)
            {
                config = PromptForConfig();
                SaveConfig(config);
            }
            else
            {
                Console.Write("    Saved credentials found. Use them? (y/n, default y): ");
                var useSaved = Console.ReadLine();
                if (useSaved?.Trim().ToLower() == "n")
                {
                    config = PromptForConfig();
                    SaveConfig(config);
                }
            }

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("User-Agent", "migration-script");
            var githubAuth = new AuthenticationHeaderValue("token", config.GithubToken);
            var codebergAuth = new AuthenticationHeaderValue("token", config.CodebergToken);

            string userLogin = "";
            var allRepos = new List<GithubRepo>();

            try
            {
                Console.WriteLine("\n>>> Fetching user profile and repository list...");

                var userRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
                userRequest.Headers.Authorization = githubAuth;
                var userResponse = await httpClient.SendAsync(userRequest);
                userResponse.EnsureSuccessStatusCode();
                var userContent = await userResponse.Content.ReadAsStringAsync();
                using var userDoc = JsonDocument.Parse(userContent);
                var userRoot = userDoc.RootElement;

                userLogin = userRoot.GetProperty("login").GetString();

                int page = 1;
                while (true)
                {
                    var uri = $"https://api.github.com/user/repos?per_page=100&page={page}";
                    var request = new HttpRequestMessage(HttpMethod.Get, uri);
                    request.Headers.Authorization = githubAuth;

                    var response = await httpClient.SendAsync(request);
                    if (!response.IsSuccessStatusCode)
                        break;

                    var content = await response.Content.ReadAsStringAsync();
                    var pageRepos = JsonSerializer.Deserialize<List<GithubRepo>>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    if (pageRepos == null || pageRepos.Count == 0)
                        break;

                    allRepos.AddRange(pageRepos);

                    if (pageRepos.Count < 100)
                        break;
                    page++;
                }

                Console.WriteLine($"    Retrieved {allRepos.Count} repositories.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Could not reach GitHub API: {ex.Message}");
                Console.ReadLine();
                return;
            }

            var uniqueOwners = allRepos.Select(r => r.Owner?.Login).Where(l => !string.IsNullOrEmpty(l)).Distinct().ToList();

            var potentialOwners = new List<string>();
            if (uniqueOwners.Contains(userLogin))
            {
                potentialOwners.Add(userLogin);
                uniqueOwners.Remove(userLogin);
            }

            var orgsLogins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var orgsRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user/orgs");
                orgsRequest.Headers.Authorization = githubAuth;
                var orgsResponse = await httpClient.SendAsync(orgsRequest);
                if (orgsResponse.IsSuccessStatusCode)
                {
                    var orgsContent = await orgsResponse.Content.ReadAsStringAsync();
                    var orgs = JsonSerializer.Deserialize<List<GithubOrg>>(orgsContent);
                    if (orgs != null)
                    {
                        foreach (var org in orgs)
                            orgsLogins.Add(org.Login);
                    }
                }
            }
            catch { }

            var orgsList = uniqueOwners.Where(o => orgsLogins.Contains(o)).ToList();
            var othersList = uniqueOwners.Where(o => !orgsLogins.Contains(o)).ToList();

            potentialOwners.AddRange(orgsList);
            potentialOwners.AddRange(othersList);

            var selectedOwners = new List<string>();
            Console.WriteLine("\n    Select which owners to migrate repositories from:");
            foreach (var owner in potentialOwners)
            {
                string label = owner == userLogin ? " (You)" : (orgsLogins.Contains(owner) ? " (Org)" : " (External/Collaborator)");
                Console.Write($"    - Allow '{owner}'{label}? (y/n, default y): ");
                var input = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(input) || input.Trim().ToLower() == "y")
                {
                    selectedOwners.Add(owner);
                }
            }

            var ownerMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (selectedOwners.Count > 0)
            {
                Console.WriteLine("\n    Configure target names on Codeberg:");
                foreach (var owner in selectedOwners)
                {
                    string defaultTarget = (owner == userLogin) ? config.CodebergUsername : owner;
                    Console.Write($"    - Target name for '{owner}' (default '{defaultTarget}'): ");
                    var target = Console.ReadLine();
                    ownerMapping[owner] = string.IsNullOrWhiteSpace(target) ? defaultTarget : target.Trim();
                }
            }

            Console.Write("    Include forks? (y/n, default n): ");
            string includeForksInput = Console.ReadLine();
            bool includeForks = includeForksInput?.Trim().ToLower() == "y";

            bool enablePushMirror = false;
            string pushMirrorInterval = "8h0m0s";
            if (!removeMirrorsOnly)
            {
                Console.Write("    Enable Push Mirroring back to GitHub? (y/n, default n): ");
                enablePushMirror = Console.ReadLine()?.Trim().ToLower() == "y";
                if (enablePushMirror)
                {
                    Console.Write("    Push mirror interval (e.g. 8h0m0s, default 8h0m0s): ");
                    var intervalInput = Console.ReadLine()?.Trim().ToLower();
                    if (!string.IsNullOrWhiteSpace(intervalInput) && intervalInput != "y")
                        pushMirrorInterval = intervalInput;
                }
            }

            bool removePushMirrors = removeMirrorsOnly;
            if (!removeMirrorsOnly)
            {
                Console.Write("    Remove all existing push mirror settings from each pushed repository? (y/n, default n): ");
                removePushMirrors = Console.ReadLine()?.Trim().ToLower() == "y";
            }

            Console.Write("    Enter description prefix (e.g. [MIRROR] , leave empty for none): ");
            string descriptionPrefix = Console.ReadLine() ?? "";

            Console.WriteLine("");
            Console.WriteLine("    ----------------------------------------------");
            Console.WriteLine($"    User on Github          : {userLogin}");
            Console.WriteLine($"    User on Codeberg        : {config.CodebergUsername}");
            Console.WriteLine($"    Using description prefix: {(string.IsNullOrEmpty(descriptionPrefix) ? "none" : descriptionPrefix)}");
            Console.WriteLine($"    Include Forks           : {(includeForks ? "Yes" : "No")}");
            Console.WriteLine($"    Push Mirroring          : {(enablePushMirror ? $"Enabled ({pushMirrorInterval})" : "Disabled")}");
            Console.WriteLine($"    Remove Existing Mirrors : {(removePushMirrors ? "Yes" : "No")}");
            Console.WriteLine($"    Mode                    : {(removeMirrorsOnly ? "Remove mirrors only" : "Migrate repositories")}");

            if (ownerMapping.Count == 0)
            {
                Console.WriteLine("    Migrating repos owned by: NONE (Script will exit)");
                Console.ReadLine();
                return;
            }
            else
            {
                Console.WriteLine("    Migrating owners mapping:");
                foreach (var kvp in ownerMapping)
                    Console.WriteLine($"      {kvp.Key} -> {kvp.Value}");
            }

            Console.WriteLine("    ----------------------------------------------");
            Console.WriteLine("");
            Console.WriteLine("    Press ENTER to continue, or Ctrl+C to abort.");
            Console.WriteLine("");
            Console.ReadLine();

            Console.WriteLine(">>> Working...");

            foreach (var repo in allRepos)
            {
                if (!ownerMapping.ContainsKey(repo.Owner?.Login))
                    continue;

                if (!includeForks && repo.Fork)
                    continue;

                string visibility = repo.Private ? "private" : "public";
                string forkStatus = repo.Fork ? "fork" : "source";

                string targetOwner = ownerMapping[repo.Owner.Login];

                if (removeMirrorsOnly)
                {
                    Console.Write($">>> Cleaning mirrors: {targetOwner}/{repo.Name}...");
                    await RemovePushMirrorsAsync(httpClient, targetOwner, repo.Name, codebergAuth);
                    continue;
                }

                Console.Write($">>> Processing: {repo.Name} ({visibility}, {forkStatus}) -> {targetOwner}...");

                string description = $"{descriptionPrefix}{repo.Description}";

                var payload = new
                {
                    auth_username = config.GithubUsername,
                    auth_token = config.GithubToken,
                    clone_addr = repo.CloneUrl,
                    @private = repo.Private,
                    repo_name = repo.Name,
                    repo_owner = targetOwner,
                    service = "github",
                    description = description,
                };

                var jsonPayload = JsonSerializer.Serialize(payload);
                var postRequest = new HttpRequestMessage(HttpMethod.Post, "https://codeberg.org/api/v1/repos/migrate");
                postRequest.Headers.Authorization = codebergAuth;
                postRequest.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                try
                {
                    var postResponse = await httpClient.SendAsync(postRequest);
                    bool shouldSetupMirror = false;

                    if (postResponse.IsSuccessStatusCode)
                    {
                        Console.WriteLine(" Success!");
                        shouldSetupMirror = true;
                    }
                    else
                    {
                        var statusCode = (int)postResponse.StatusCode;
                        if (statusCode == 409)
                        {
                            Console.WriteLine(" Already exists on Codeberg.");
                            shouldSetupMirror = true;
                        }
                        else if (statusCode == 403)
                        {
                            Console.WriteLine(" Error! Forbidden.");
                        }
                        else
                        {
                            var errorContent = await postResponse.Content.ReadAsStringAsync();
                            try
                            {
                                using var errDoc = JsonDocument.Parse(errorContent);
                                string msg = errDoc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : "Unknown error";
                                Console.WriteLine($" Error: {msg} (HTTP {statusCode})");
                            }
                            catch
                            {
                                Console.WriteLine($" Error: Unknown! HTTP {statusCode}");
                            }
                        }
                    }

                    if (shouldSetupMirror && removePushMirrors)
                    {
                        await RemovePushMirrorsAsync(httpClient, targetOwner, repo.Name, codebergAuth);
                    }

                    if (shouldSetupMirror && enablePushMirror)
                    {
                        await SetupPushMirrorAsync(httpClient, targetOwner, repo.Owner.Login, repo.Name, pushMirrorInterval, codebergAuth, config);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($" Error: {ex.Message}");
                }
            }

            Console.WriteLine("");
            Console.WriteLine(">>> Migration script completed!");
            Console.ReadLine();
        }

        static async Task SetupPushMirrorAsync(HttpClient client, string targetOwner, string githubOwner, string repoName, string interval, AuthenticationHeaderValue auth, Config config)
        {
            Console.Write($"    -> Setting up push mirror back to GitHub...");

            var url = $"https://codeberg.org/api/v1/repos/{targetOwner}/{repoName}/push_mirrors";

            var payload = new
            {
                remote_address = $"https://github.com/{githubOwner}/{repoName}.git",
                remote_username = config.GithubUsername,
                remote_password = config.GithubToken,
                interval = interval,
            };

            var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = auth;
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            try
            {
                var response = await client.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    Console.WriteLine(" Done!");
                }
                else
                {
                    Console.WriteLine($" Failed! (HTTP {(int)response.StatusCode})");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($" Error: {ex.Message}");
            }
        }

        static async Task RemovePushMirrorsAsync(HttpClient client, string owner, string repoName, AuthenticationHeaderValue auth)
        {
            Console.Write("    -> Removing existing push mirrors...");
            var url = $"https://codeberg.org/api/v1/repos/{owner}/{repoName}/push_mirrors";

            try
            {
                using var getRequest = new HttpRequestMessage(HttpMethod.Get, url);
                getRequest.Headers.Authorization = auth;
                using var getResponse = await client.SendAsync(getRequest);
                if (!getResponse.IsSuccessStatusCode)
                {
                    if (getResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
                        Console.WriteLine(" Repository not found under this Codeberg owner (check the target owner mapping).");
                    else
                        Console.WriteLine($" Failed to list mirrors (HTTP {(int)getResponse.StatusCode})");
                    return;
                }

                using var mirrorsDoc = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());
                var mirrors = mirrorsDoc.RootElement;
                if (mirrors.ValueKind != JsonValueKind.Array || mirrors.GetArrayLength() == 0)
                {
                    Console.WriteLine(" None found.");
                    return;
                }

                int removed = 0;
                foreach (var mirror in mirrors.EnumerateArray())
                {
                    if (!mirror.TryGetProperty("remote_name", out var remoteName) || string.IsNullOrWhiteSpace(remoteName.GetString()))
                    {
                        Console.WriteLine(" Could not remove a mirror because its remote name was missing from the API response.");
                        continue;
                    }

                    var encodedRemoteName = Uri.EscapeDataString(remoteName.GetString()!);
                    using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, $"{url}/{encodedRemoteName}");
                    deleteRequest.Headers.Authorization = auth;
                    using var deleteResponse = await client.SendAsync(deleteRequest);
                    if (deleteResponse.IsSuccessStatusCode)
                        removed++;
                    else
                        Console.WriteLine($" Failed to remove mirror '{remoteName.GetString()}' (HTTP {(int)deleteResponse.StatusCode}).");
                }

                Console.WriteLine($" Removed {removed} of {mirrors.GetArrayLength()} mirror(s).");
            }
            catch (Exception ex)
            {
                Console.WriteLine($" Error: {ex.Message}");
            }
        }

        private static Config LoadConfig()
        {
            if (!System.IO.File.Exists(CONFIG_FILE))
                return null;
            try
            {
                var json = System.IO.File.ReadAllText(CONFIG_FILE);
                return JsonSerializer.Deserialize<Config>(json);
            }
            catch
            {
                return null;
            }
        }

        private static void SaveConfig(Config config)
        {
            try
            {
                var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                System.IO.File.WriteAllText(CONFIG_FILE, json);
                Console.WriteLine($"    Credentials saved to {CONFIG_FILE}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    [WARNING] Could not save config: {ex.Message}");
            }
        }

        private static Config PromptForConfig()
        {
            Console.WriteLine("\n    --- Configuration Setup ---");
            Console.Write("    GitHub Username: ");
            string ghUser = Console.ReadLine();
            Console.Write("    GitHub Token: ");
            string ghToken = Console.ReadLine();
            Console.Write("    Codeberg Username: ");
            string cbUser = Console.ReadLine();
            Console.Write("    Codeberg Token: ");
            string cbToken = Console.ReadLine();

            return new Config
            {
                GithubUsername = ghUser?.Trim(),
                GithubToken = ghToken?.Trim(),
                CodebergUsername = cbUser?.Trim(),
                CodebergToken = cbToken?.Trim(),
            };
        }
    }

    public class Config
    {
        public string GithubUsername { get; set; }
        public string GithubToken { get; set; }
        public string CodebergUsername { get; set; }
        public string CodebergToken { get; set; }
    }

    public class GithubRepo
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; }

        [JsonPropertyName("private")]
        public bool Private { get; set; }

        [JsonPropertyName("fork")]
        public bool Fork { get; set; }

        [JsonPropertyName("clone_url")]
        public string CloneUrl { get; set; }

        [JsonPropertyName("owner")]
        public GithubOwner Owner { get; set; }
    }

    public class GithubOwner
    {
        [JsonPropertyName("login")]
        public string Login { get; set; }
    }

    public class GithubOrg
    {
        [JsonPropertyName("login")]
        public string Login { get; set; }
    }
}
