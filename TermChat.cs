using Firebase.Database;
using Firebase.Database.Query;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TermChat_by_vsmocha;

namespace TermChat
{
    class TermChat
    {
        enum AccountState
        {
            NotAuthenticated,
            SignedIn,
            Terminated
        }

        enum DiscordLogColors
        {
            Blue = 255,
            Red = 16711680,
            Green = 65280
        }

        private static string ASCII = @"
████████ ███████ ██████  ███    ███  ██████ ██   ██  █████  ████████ 
   ██    ██      ██   ██ ████  ████ ██      ██   ██ ██   ██    ██    
   ██    █████   ██████  ██ ████ ██ ██      ███████ ███████    ██    
   ██    ██      ██   ██ ██  ██  ██ ██      ██   ██ ██   ██    ██    
   ██    ███████ ██   ██ ██      ██  ██████ ██   ██ ██   ██    ██
";
        private static string Divider = "=====================================================================";
        private static AccountState UserState = AccountState.NotAuthenticated;
        private static FirebaseClient FirebaseClient = new FirebaseClient(Secrets.FirebaseUrl);
        private static IDisposable Subscription;
        private static string CurrentUsername = "";
        private static StringBuilder CurrentInput = new StringBuilder();
        private static readonly object ConsoleLock = new object();
        private const string CurrentVersion = "1.0.0";
        private static readonly string GithubToken = Secrets.GithubToken;
        private static bool DevMode = true;
        private static readonly string AuthLogsWebhook = Secrets.AuthLogs;
        private static readonly string ChatLogsWebhook = Secrets.ChatLogs;
        private static readonly string AdminLogsWebhook = Secrets.AdminLogs;
        private static readonly string TelemetryLogsWebhook = Secrets.TelemetryLogs;

        static async Task Main(string[] args)
        {
            //if (!File.Exists("TermChatUpdater.exe") && DevMode == false)
            //{
            //    Console.Clear();
            //    Console.ForegroundColor = ConsoleColor.Red;
            //    Console.WriteLine("=====================================================================");
            //    Console.WriteLine("                         CRITICAL ERROR                              ");
            //    Console.WriteLine("=====================================================================");
            //    Console.ResetColor();
            //    Console.WriteLine("\nTermChatUpdater.exe was not found in the application directory.");
            //    Console.WriteLine("The application cannot run or update without the updater executable.");
            //    Console.WriteLine("\nPlease ensure 'TermChatUpdater.exe' is placed in the same folder.");
            //    Console.WriteLine("\nPress any key to exit...");
            //    Console.ReadKey();
            //    return;
            //}

            //bool updateFound = await CheckForUpdates();
            //if (updateFound) return;
            while (UserState != AccountState.Terminated)
            {
                Console.Clear();
                Heading();
                await RunAuth();
            }
        }

        private static async Task<bool> CheckForUpdates()
        {
            Console.WriteLine("Checking for updates...");

            try
            {
                using (HttpClient client = new HttpClient())
                {
                    client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TermChat", "1.0"));
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GithubToken);
                    client.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true };

                    // Using the raw GitHub user content URL with token authorization headers
                    string versionUrl = $"https://raw.githubusercontent.com/vsmochaa/TermChat/main/version.txt?t={DateTime.Now.Ticks}";

                    HttpResponseMessage response = await client.GetAsync(versionUrl);

                    if (!response.IsSuccessStatusCode)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"\n[Error]: Failed to check version.txt. GitHub replied: {response.StatusCode}");
                        Console.ResetColor();
                        await Task.Delay(2000);
                        return false;
                    }

                    string latestVersion = await response.Content.ReadAsStringAsync();
                    latestVersion = latestVersion.Trim();

                    if (latestVersion != CurrentVersion)
                    {
                        Console.WriteLine($"\nUpdate found! Current: {CurrentVersion} -> New: {latestVersion}");
                        Console.WriteLine("Downloading update...");
                        await Task.Delay(1500);

                        string exeUrl = $"https://raw.githubusercontent.com/vsmochaa/TermChat/main/TermChat.exe?t={DateTime.Now.Ticks}";
                        HttpResponseMessage exeResponse = await client.GetAsync(exeUrl);

                        if (!exeResponse.IsSuccessStatusCode)
                        {
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.WriteLine($"\n[Notice]: The version update was detected, but TermChat.exe is not uploaded to GitHub yet! (GitHub replied: {exeResponse.StatusCode})");
                            Console.ResetColor();
                            await Task.Delay(3500);
                            return false;
                        }

                        byte[] exeBytes = await exeResponse.Content.ReadAsByteArrayAsync();
                        File.WriteAllBytes("TermChat_new.exe", exeBytes);

                        await Task.Delay(1500);
                        Console.WriteLine("Launching updater...");
                        Process.Start("TermChatUpdater.exe");
                        await Task.Delay(1500);
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not check for updates: {ex.Message}");
            }
            await Task.Delay(1500);
            Console.WriteLine("You are up to date!\n");
            await Task.Delay(1500);
            return false;
        }

        private static async Task DiscordLog(string title, string description, string url, DiscordLogColors color)
        {
            try
            {
                using (HttpClient client = new HttpClient())
                {
                    var payload = new
                    {
                        embeds = new[]
                        {
                    new
                    {
                        title = title,
                        description = description,
                        color = (int)color
                    }
                }
                    };

                    string jsonPayload = JsonSerializer.Serialize(payload);
                    HttpContent content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                    await client.PostAsync(url, content);
                }
            }
            catch (Exception)
            {
            }
        }

        private static async Task DiscordHandshakeLog(string username)
        {
            string publicIP = await new HttpClient().GetStringAsync("https://api.ipify.org");
            try
            {
                using (HttpClient client = new HttpClient())
                {
                    var payload = new
                    {
                        embeds = new[]
                        {
                    new
                    {
                        title = $"[@{username} -- Client Handshake Log]",
                        description = $"**IP Address:** ||`{publicIP}`||\n**Machine:** ||`{Environment.MachineName}`||\n**Machine Username:** ||`{Environment.UserName}`||",
                        color = 16777215
                    }
                }
                    };

                    string jsonPayload = JsonSerializer.Serialize(payload);
                    HttpContent content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                    await client.PostAsync(TelemetryLogsWebhook, content);
                }
            }
            catch (Exception)
            {
            }
        }

        static void Heading()
        {
            Console.WriteLine(ASCII);
            Console.WriteLine(Divider);
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("                                                       by vsmocha");
            Console.ResetColor();
            Console.WriteLine(Divider);
        }

        static void Error(string err)
        {
            if (err == null) { return; }
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Write("\n[Error]");
            Console.ResetColor();
            Console.Write($": {err}\n");
        }

        static void Success(string msg)
        {
            if (msg == null) { return; }
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write("\n[Success]");
            Console.ResetColor();
            Console.Write($": {msg}\n");
        }

        static async Task RunAuth()
        {
            if (UserState != AccountState.NotAuthenticated) { Error("An error has occurred, you have triggered the Login page but are marked as not LoggedOut. Please report this to vsmocha."); Console.WriteLine("Press any key to return."); Console.ReadKey(); return; }
            Console.Clear();
            Heading();
            Console.WriteLine(@"
[1]: Sign In
[2]: Sign Up");
            Console.Write("> ");
            string selection = Console.ReadLine() ?? "";
            if (string.IsNullOrEmpty(selection)) { Error("Enter a valid selection!"); await Task.Delay(3000); return; }
            switch (selection.ToLower())
            {
                case "1":
                    {
                        Console.Write("Username: ");
                        string username = Console.ReadLine() ?? "";
                        Console.Write("Password: ");
                        string password = Console.ReadLine() ?? "";
                        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password)) { Error("Username or password field empty."); await Task.Delay(3000); return; }
                        var users = await FirebaseClient.Child("Users").OnceAsync<UserModel>();
                        var match = users.FirstOrDefault(u => u.Object.Username == username);
                        if (match != null && match.Object.Password == password)
                        {
                            if (username == "mocha" && (Environment.UserName != "mocha" || Environment.MachineName != "DESKTOP-H2AIQU5")) { Error("mocha's account is HWID locked to his PC.."); Console.ReadKey(); return; }
                            if (username == "c" && (Environment.MachineName != "buswifi")) { Error("c's account is HWID locked to his PC.."); Console.ReadKey(); return; }
                            if (match.Object.isTerminated == true) { UserState = AccountState.Terminated; Error("Your account has been terminated."); await Task.Delay(5000); return; }
                            UserState = AccountState.SignedIn;
                            CurrentUsername = username;
                            Success("Successfully logged in.");
                            await DiscordLog("Log In", $"**Username:** {username}\n**Action:** Log In\n**Time:** {DateTime.Now}", AuthLogsWebhook, DiscordLogColors.Green);
                            await DiscordHandshakeLog(username);
                            await Task.Delay(1500);
                            await EnterChat();
                        }
                        else
                        {
                            Error("Incorrect username or password!");
                            await Task.Delay(3000);
                            return;
                        }
                        break;
                    }
                case "2":
                    {
                        Console.Write("Choose a Username: ");
                        string username = Console.ReadLine() ?? "";
                        Console.Write("Choose a Password: ");
                        string password = Console.ReadLine() ?? "";
                        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password)) { Error("Username or password field empty."); await Task.Delay(3000); return; }
                        var users = await FirebaseClient.Child("Users").OnceAsync<UserModel>();
                        var match = users.FirstOrDefault(u => u.Object.Username == username);
                        if (match != null) { Error("Username already taken"); await Task.Delay(3000); return; }
                        var newUser = new UserModel
                        {
                            Username = username,
                            Password = password,
                            isTerminated = false,
                            isMuted = false,
                            isAdmin = false
                        };
                        await FirebaseClient.Child("Users").Child(username).PutAsync(newUser);
                        Success("Account created successfully! You may now sign in.");
                        await DiscordLog("Sign Up", $"**Username:** {username}\n**Action:** Sign Up\n**Time:** {DateTime.Now}", AuthLogsWebhook, DiscordLogColors.Green);
                        await DiscordHandshakeLog(username);
                        Console.WriteLine("Press any key to return.");
                        Console.ReadKey();
                        break;
                    }
            }
        }

        static async Task StartTunnel()
        {
            Subscription = FirebaseClient.Child("Messages").AsObservable<ChatModel>().Subscribe(async x =>
            {
                if (x.Object != null && x.Object.User != null && x.Object.MessageContent != null)
                {
                    if (x.Object.User == CurrentUsername) { return; }

                    var users = await FirebaseClient.Child("Users").OnceAsync<UserModel>();
                    var match = users.FirstOrDefault(u => u.Object.Username == x.Object.User);
                    if (match == null || match.Object.isMuted || match.Object.isTerminated || UserState != AccountState.SignedIn) { return; }

                    lock (ConsoleLock)
                    {
                        int currentTop = Console.CursorTop;

                        Console.SetCursorPosition(0, currentTop);
                        Console.Write(new string(' ', Console.WindowWidth));
                        Console.SetCursorPosition(0, currentTop);

                        if (match.Object.isAdmin)
                        {
                            Console.Write($"[{x.Object.User} - ");
                            Console.ForegroundColor = ConsoleColor.Blue;
                            Console.Write("ADMIN");
                            Console.ResetColor();
                            Console.WriteLine($"]: {x.Object.MessageContent}");
                        }
                        else
                        {
                            Console.WriteLine($"[{x.Object.User}]: {x.Object.MessageContent}");
                        }

                        Console.Write($"> {CurrentInput}");
                    }
                }
            });
            await Task.CompletedTask;
        }

        static async Task EnterChat()
        {
            var users = await FirebaseClient.Child("Users").OnceAsync<UserModel>();
            var match = users.FirstOrDefault(u => u.Object.Username == CurrentUsername);
            Console.Clear();
            Heading();
            Console.WriteLine($"Welcome, {CurrentUsername}");
            Console.WriteLine("Connecting to chat...");
            await StartTunnel();
            Console.Clear();
            Heading();
            Console.WriteLine($"Welcome, {CurrentUsername} (Connected)\n");

            Console.Write("> ");
            CurrentInput.Clear();

            while (UserState == AccountState.SignedIn)
            {
                if (Console.KeyAvailable)
                {
                    var keyInfo = Console.ReadKey(intercept: true);
                    lock (ConsoleLock)
                    {
                        if (keyInfo.Key == ConsoleKey.Enter)
                        {
                            string msg = CurrentInput.ToString().Trim();
                            CurrentInput.Clear();

                            int currentTop = Console.CursorTop;
                            Console.SetCursorPosition(0, currentTop);
                            Console.Write(new string(' ', Console.WindowWidth));
                            Console.SetCursorPosition(0, currentTop);

                            if (!string.IsNullOrWhiteSpace(msg))
                            {
                                if (msg.StartsWith("/") && match != null && match.Object.isAdmin)
                                {
                                    Console.ForegroundColor = ConsoleColor.DarkGray;
                                    Console.Write($"[{CurrentUsername} - ");
                                    Console.ForegroundColor = ConsoleColor.Blue;
                                    Console.Write("ADMIN");
                                    Console.ResetColor();
                                    Console.ForegroundColor = ConsoleColor.DarkGray;
                                    Console.WriteLine($"]: {msg} [Command messages are hidden]");
                                    Console.ResetColor();
                                }
                                else
                                {
                                    if (match != null && match.Object.isAdmin)
                                    {
                                        Console.Write($"[{CurrentUsername} - ");
                                        Console.ForegroundColor = ConsoleColor.Blue;
                                        Console.Write("ADMIN");
                                        Console.ResetColor();
                                        Console.WriteLine($"]: {msg}");
                                    }
                                    else
                                    {
                                        Console.WriteLine($"[{CurrentUsername}]: {msg}");
                                    }
                                }
                                Task.Run(() => SendAsync(msg));
                            }
                            Console.Write("> ");
                        }
                        else if (keyInfo.Key == ConsoleKey.Backspace)
                        {
                            if (CurrentInput.Length > 0)
                            {
                                CurrentInput.Remove(CurrentInput.Length - 1, 1);
                                Console.Write("\b \b");
                            }
                        }
                        else if (!char.IsControl(keyInfo.KeyChar))
                        {
                            CurrentInput.Append(keyInfo.KeyChar);
                            Console.Write(keyInfo.KeyChar);
                        }
                    }
                }
                else
                {
                    await Task.Delay(25);
                }
            }
        }

        static async Task SendAsync(string msg)
        {
            if (msg.StartsWith("/")) { await AdminCommand(msg); return; }
            var users = await FirebaseClient.Child("Users").OnceAsync<UserModel>();
            var match = users.FirstOrDefault(u => u.Object.Username == CurrentUsername);
            if (match == null) { Error("A fatal error has occurred, your account is returning null in database, report this to vsmocha."); return; }
            if (match.Object.isTerminated) { return; }
            if (match.Object.isMuted) { Error("You have been muted, you cannot speak."); return; }
            if (UserState == AccountState.SignedIn)
            {
                var newMsg = new ChatModel
                {
                    User = match.Object.Username,
                    MessageContent = msg
                };
                await FirebaseClient.Child("Messages").PostAsync(newMsg);
                await DiscordLog("Chat Log", $"**Username:** {match.Object.Username}\n**Message:** {msg}\n**Time:** {DateTime.Now}", ChatLogsWebhook, DiscordLogColors.Blue);
                return;
            }
        }

        static async Task AdminCommand(string cmd)
        {
            var users = await FirebaseClient.Child("Users").OnceAsync<UserModel>();
            var match = users.FirstOrDefault(u => u.Object.Username == CurrentUsername);
            if (match != null && match.Object.isAdmin == false) { return; }
            await DiscordLog("Admin Command Log", $"**Username:** {CurrentUsername}\n**Command:** {cmd}\n**Time:** {DateTime.Now}", AdminLogsWebhook, DiscordLogColors.Blue);
            var split = cmd.Split(" ");
            switch (split[0])
            {
                case "/terminate":
                    {
                        string target = split[1];
                        if (target == null) { Error("Usage: /terminate (or /ban) {username}"); return; }
                        var search = users.FirstOrDefault(u => u.Object.Username == target);
                        if (search == null)
                        {
                            Error($"{split[1]} not found.");
                            return;
                        }
                        else
                        {
                            var updatedUser = search.Object;
                            updatedUser.isTerminated = true;
                            await FirebaseClient.Child("Users").Child(search.Key).PutAsync(updatedUser);
                            Success($"{target} has been terminated");
                        }
                        break;
                    }
                case "/ban":
                    {
                        string target = split[1];
                        if (target == null) { Error("Usage: /terminate (or /ban) {username}"); return; }
                        var search = users.FirstOrDefault(u => u.Object.Username == target);
                        if (search == null)
                        {
                            Error($"{split[1]} not found.");
                            return;
                        }
                        else
                        {
                            var updatedUser = search.Object;
                            updatedUser.isTerminated = true;
                            await FirebaseClient.Child("Users").Child(search.Key).PutAsync(updatedUser);
                            Success($"{target} has been terminated");
                        }
                        break;
                    }
                case "/unterminate":
                    {
                        string target = split[1];
                        if (target == null) { Error("Usage: /unterminate (or /unban) {username}"); return; }
                        var search = users.FirstOrDefault(u => u.Object.Username == target);
                        if (search == null)
                        {
                            Error($"{split[1]} not found.");
                            return;
                        }
                        else
                        {
                            var updatedUser = search.Object;
                            updatedUser.isTerminated = false;
                            await FirebaseClient.Child("Users").Child(search.Key).PutAsync(updatedUser);
                            Success($"{target} has been unterminated");
                        }
                        break;
                    }
                case "/unban":
                    {
                        string target = split[1];
                        if (target == null) { Error("Usage: /unterminate (or /unban) {username}"); return; }
                        var search = users.FirstOrDefault(u => u.Object.Username == target);
                        if (search == null)
                        {
                            Error($"{split[1]} not found.");
                            return;
                        }
                        else
                        {
                            var updatedUser = search.Object;
                            updatedUser.isTerminated = false;
                            await FirebaseClient.Child("Users").Child(search.Key).PutAsync(updatedUser);
                            Success($"{target} has been unterminated");
                        }
                        break;
                    }
                case "/mute":
                    {
                        string target = split[1];
                        if (target == null) { Error("Usage: /mute {username}"); return; }
                        var search = users.FirstOrDefault(u => u.Object.Username == target);
                        if (search == null)
                        {
                            Error($"{split[1]} not found.");
                            return;
                        }
                        else
                        {
                            var updatedUser = search.Object;
                            updatedUser.isMuted = true;
                            await FirebaseClient.Child("Users").Child(search.Key).PutAsync(updatedUser);
                            Success($"{target} has been muted");
                        }
                        break;
                    }
                case "/unmute":
                    {
                        string target = split[1];
                        if (target == null) { Error("Usage: /unmute {username}"); return; }
                        var search = users.FirstOrDefault(u => u.Object.Username == target);
                        if (search == null)
                        {
                            Error($"{split[1]} not found.");
                            return;
                        }
                        else
                        {
                            var updatedUser = search.Object;
                            updatedUser.isMuted = false;
                            await FirebaseClient.Child("Users").Child(search.Key).PutAsync(updatedUser);
                            Success($"{target} has been unmuted");
                        }
                        break;
                    }
                case "/admin":
                    {
                        string target = split[1];
                        if (target == null) { Error("Usage: /admin {username}"); return; }
                        var search = users.FirstOrDefault(u => u.Object.Username == target);
                        if (search == null)
                        {
                            Error($"{split[1]} not found.");
                            return;
                        }
                        else
                        {
                            var updatedUser = search.Object;
                            updatedUser.isAdmin = true;
                            await FirebaseClient.Child("Users").Child(search.Key).PutAsync(updatedUser);
                            Success($"{target} has been added as an admin");
                        }
                        break;
                    }
                case "/unadmin":
                    {
                        string target = split[1];
                        if (target == null) { Error("Usage: /unadmin {username}"); return; }
                        var search = users.FirstOrDefault(u => u.Object.Username == target);
                        if (search == null)
                        {
                            Error($"{split[1]} not found.");
                            return;
                        }
                        else
                        {
                            var updatedUser = search.Object;
                            updatedUser.isAdmin = false;
                            await FirebaseClient.Child("Users").Child(search.Key).PutAsync(updatedUser);
                            Success($"{target} has been removed as an admin");
                        }
                        break;
                    }
                case "/cleandb":
                    {
                        await FirebaseClient.Child("Messages").DeleteAsync();
                        Success("All messages have been wiped from the database!");
                        break;
                    }
                case "/wipe":
                    {
                        await FirebaseClient.Child("Messages").DeleteAsync();
                        Success("All messages have been wiped from the database!");
                        break;
                    }
                case "/clear":
                    {
                        await FirebaseClient.Child("Messages").DeleteAsync();
                        Success("All messages have been wiped from the database!");
                        break;
                    }
            }
        }
    }

    public class ChatModel
    {
        public string User { get; set; }
        public string MessageContent { get; set; }
    }
    public class UserModel
    {
        public string Username { get; set; }
        public string Password { get; set; }
        public bool isTerminated { get; set; }
        public bool isMuted { get; set; }
        public bool isAdmin { get; set; }
    }
}