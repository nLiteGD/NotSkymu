// NSkype plugin for Skymu.
// Talks to a plain HTTP/JSON backend (your Server/server.js) — no Microsoft
// code, no Skype client binaries involved. Implements ICore fully,
// IListManagement for contact search/add, and ICall with real two-way audio
// via genuine WebRTC (SIPSorcery) — real SDP offer/answer negotiated through
// server.js's actual call-signaling endpoints (callInvitation/callAcceptance
// mediaContent for the initial exchange; mediaNegotiation/mediaAnswer/
// mediaAcknowledgement exist server-side for renegotiation but aren't needed
// for a basic audio call and aren't used here). This is required, not
// optional, for interop with real Skype-family clients on the other end —
// they only understand SDP-based negotiation. An earlier revision of this
// file used a custom WebSocket "audio relay" instead; that mechanism never
// existed server-side (no handleCallAudioUpgrade or equivalent anywhere in
// server.js) and calls against a real client would ring, then get torn down
// during that client's own media-answer timeout, matching exactly what was
// reported. Video calling is NOT implemented — StartCall always negotiates
// audio-only regardless of is_video_call, and SetVideoEnabled is a no-op.
//
// IMPORTANT: I could not compile or run this against SIPSorcery/NAudio in
// the sandbox I wrote it in (no .NET toolchain or NuGet access there). Every
// request/response shape (callInvitation.mediaContent, callAcceptance.
// mediaContent, the callNotification/callAcceptance event payloads) is
// verified directly against server.js's actual route handlers, not guessed.
// The SIPSorcery API calls (createOffer/setLocalDescription/
// setRemoteDescription/SendAudio, RTCSessionDescriptionInit, SetDescription
// ResultEnum) are written to SIPSorcery 6.0.4's public API to the best of
// available knowledge, but exact method signatures can drift slightly
// between versions — if something doesn't compile, that's most likely
// where to look first.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using Yggdrasil;
using Yggdrasil.Bottles;
using Yggdrasil.Enumerations;
using Yggdrasil.Models;

namespace NSkype
{
    public class Core : ICore, ICall, IListManagement
    {
        // ---------------------------------------------------------------
        // TODO: point this at your server's publicHost from config.json
        // ---------------------------------------------------------------
        private const string BaseUrl = "https://notskype.hidenfree.com:443";

        #region ICore metadata

        public event EventHandler<DialogBottle> DialogTube;
        public event EventHandler<MessageBottle> MessageTube;
        public event EventHandler<ListBottle> ListTube;

        public string Name => "NSkype";
        public string InternalName => "nskype";
        public bool SupportsServers => false;
        public int TypingTimeout => 5000;
        public int TypingRepeat => 9000;

        public AuthTypeInfo[] AuthenticationTypes => new[]
        {
            new AuthTypeInfo(AuthenticationMethod.Password)
        };

        public ObservableCollection<User> TypingUsersList { get; } = new ObservableCollection<User>();

        public ClickableConfiguration[] ClickableConfigurations => Array.Empty<ClickableConfiguration>();

        #endregion

        #region State

        private readonly HttpClient _http;
        private string _username;
        private string _accessToken;
        private string _refreshToken;
        private string _skypeToken;
        private string _registrationToken;
        private string _subscriptionId;
        private CancellationTokenSource _pollCts;
        private User Me;

        // Cache of users we've seen (contacts, message authors) so we don't
        // have to re-fetch profiles constantly.
        private readonly Dictionary<string, User> _userCache = new Dictionary<string, User>();

        // Calling state — only one call at a time is supported. Real WebRTC via
        // SIPSorcery: server.js implements genuine SDP negotiation
        // (mediaNegotiation/mediaAnswer/mediaAcknowledgement, plus offer/answer
        // bundled into call-create/accept), which is what real Skype-family
        // clients on the other end require. There is no audio-relay
        // WebSocket anywhere in server.js - that was never real; see the
        // handleCallAudioUpgrade fix for how this was diagnosed.
        private NAudio.Wave.WaveInEvent _waveIn;
        private NAudio.Wave.WaveOutEvent _waveOut;
        private NAudio.Wave.BufferedWaveProvider _playbackBuffer;
        private ActiveCall _activeCall;
        private bool _muted;
        private RTCPeerConnection _peerConnection;
        private string _pendingIncomingOffer;
        private bool _mediaConnected;

        // Set when an incoming CallNotification event arrives; consumed by AnswerCall.
        private string _pendingIncomingCallId;
        private string _pendingIncomingConvoId;

        #endregion

        public Core()
        {
            var handler = new HttpClientHandler { AllowAutoRedirect = false };
            // Only needed while your server uses a self-signed dev cert
            // (Server/cert.crt). Remove this once you have a real cert.
            handler.ServerCertificateCustomValidationCallback = (msg, cert, chain, errors) => true;
            _http = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };
        }

        public void Dispose()
        {
            _pollCts?.Cancel();
            _pollCts = null;
        }

        #region HTTP helpers

        private async Task<JsonDocument> PostFormAsync(string path, Dictionary<string, string> form)
        {
            var resp = await _http.PostAsync(path, new FormUrlEncodedContent(form));
            var text = await resp.Content.ReadAsStringAsync();
            return string.IsNullOrEmpty(text) ? null : JsonDocument.Parse(text);
        }

        private async Task<HttpResponseMessage> PostJsonRawAsync(string path, object payload)
        {
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            return await _http.PostAsync(path, content);
        }

        private async Task<JsonDocument> PostJsonAsync(string path, object payload)
        {
            var resp = await PostJsonRawAsync(path, payload);
            var text = await resp.Content.ReadAsStringAsync();
            return string.IsNullOrEmpty(text) ? null : JsonDocument.Parse(text);
        }

        private async Task<JsonDocument> PutJsonAsync(string path, object payload)
        {
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await _http.PutAsync(path, content);
            var text = await resp.Content.ReadAsStringAsync();
            return string.IsNullOrEmpty(text) ? null : JsonDocument.Parse(text);
        }

        private async Task<JsonDocument> GetJsonAsync(string path)
        {
            var resp = await _http.GetAsync(path);
            var text = await resp.Content.ReadAsStringAsync();
            return string.IsNullOrEmpty(text) ? null : JsonDocument.Parse(text);
        }

        private void SetAuthHeader()
        {
            _http.DefaultRequestHeaders.Remove("Authentication");
            _http.DefaultRequestHeaders.Add("Authentication", $"skypetoken={_skypeToken}");
        }

        // Manual fragment parser — netstandard2.0 doesn't have System.Web.HttpUtility.
        private static Dictionary<string, string> ParseFragment(string fragment)
        {
            var result = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(fragment)) return result;
            foreach (var pair in fragment.Split('&'))
            {
                var idx = pair.IndexOf('=');
                if (idx < 0) continue;
                var key = Uri.UnescapeDataString(pair.Substring(0, idx));
                var value = Uri.UnescapeDataString(pair.Substring(idx + 1));
                result[key] = value;
            }
            return result;
        }

        private static string Q(string s) => Uri.EscapeDataString(s ?? string.Empty);

        #endregion

        #region Auth

        public Task<string> GetQRCode() => Task.FromResult(string.Empty);

        public async Task<LoginResult> Authenticate(AuthenticationMethod auth_type, string username, string password)
        {
            username = (username ?? string.Empty).Trim().ToLowerInvariant();
            var resp = await _http.PostAsync("/ppsecure/post.srf", new FormUrlEncodedContent(
                new Dictionary<string, string> { ["username"] = username, ["passwd"] = password }));

            if (resp.StatusCode != HttpStatusCode.SeeOther || resp.Headers.Location == null)
            {
                DialogTube?.Invoke(this, new DialogBottle(DialogType.Error, "Invalid username or password."));
                return LoginResult.Failure;
            }

            var frag = ParseFragment(resp.Headers.Location.Fragment.TrimStart('#'));
            if (!frag.TryGetValue("access_token", out _accessToken))
            {
                DialogTube?.Invoke(this, new DialogBottle(DialogType.Error, "Sign-in did not return an access token."));
                return LoginResult.Failure;
            }
            frag.TryGetValue("refresh_token", out _refreshToken);
            _username = frag.TryGetValue("user_id", out var uid) ? uid : username;

            return await FinishLogin();
        }

        public async Task<LoginResult> Authenticate(SavedCredential credential)
        {
            _username = credential.User.Username;
            _refreshToken = credential.PasswordOrToken;

            using var doc = await PostFormAsync("/oauth20_token.srf", new Dictionary<string, string>
            {
                ["refresh_token"] = _refreshToken,
                ["grant_type"] = "refresh_token"
            });
            if (doc == null || !doc.RootElement.TryGetProperty("access_token", out var accessEl))
                return LoginResult.Failure;

            _accessToken = accessEl.GetString();
            doc.RootElement.TryGetProperty("refresh_token", out var refreshEl);
            _refreshToken = refreshEl.ValueKind == JsonValueKind.String ? refreshEl.GetString() : _refreshToken;

            return await FinishLogin();
        }

        public Task<LoginResult> AuthenticateTwoFA(string code) => Task.FromResult(LoginResult.Success);

        public Task<SavedCredential> StoreCredential()
        {
            if (Me == null || string.IsNullOrEmpty(_refreshToken)) return Task.FromResult<SavedCredential>(null);
            return Task.FromResult(new SavedCredential(Me, _refreshToken, AuthenticationMethod.Password, InternalName));
        }

        private async Task<LoginResult> FinishLogin()
        {
            using (var tokenDoc = await PostJsonAsync("/rps/v1/rps/skypetoken", new { access_token = _accessToken }))
            {
                if (tokenDoc == null || !tokenDoc.RootElement.TryGetProperty("skypetoken", out var tokEl))
                {
                    DialogTube?.Invoke(this, new DialogBottle(DialogType.Error, "Could not obtain a Skype token."));
                    return LoginResult.Failure;
                }
                _skypeToken = tokEl.GetString();
            }
            SetAuthHeader();

            // Register an endpoint (gives us a registration token + lets us subscribe to events)
            var epResp = await PostJsonRawAsync("/v1/users/ME/endpoints", new { });
            if (epResp.Headers.TryGetValues("Set-RegistrationToken", out var regVals))
                _registrationToken = regVals.First().Split(';')[0].Replace("registrationToken=", "");

            using (var subDoc = await PostJsonAsync("/v1/users/ME/endpoints/SELF/subscriptions", new
            {
                channelType = "httpLongPoll",
                template = "raw",
                interestedResources = new[]
                {
                    "/v1/users/ME/conversations/ALL/properties",
                    "/v1/users/ME/contacts/ALL",
                    "/v1/threads/ALL"
                }
            }))
            {
                _subscriptionId = subDoc != null && subDoc.RootElement.TryGetProperty("id", out var idEl)
                    ? idEl.GetInt32().ToString()
                    : "0";
            }

            Me = new User(_username, _username, $"8:{_username}", presence_status: PresenceStatus.Online);
            _userCache[_username] = Me;

            _pollCts = new CancellationTokenSource();
            _ = Task.Run(() => PollLoopAsync(_pollCts.Token));

            return LoginResult.Success;
        }

        #endregion

        #region Profile / contacts / conversations

        public async Task<User> GetUserInfo()
        {
            using var doc = await GetJsonAsync("/profile/v1/users/self/profile");
            if (doc != null)
            {
                var root = doc.RootElement;
                var mood = root.TryGetProperty("mood", out var moodEl) ? moodEl.GetString() : null;
                var displayName = root.TryGetProperty("displayname", out var dnEl) ? dnEl.GetString() : _username;
                Me = new User(displayName, _username, $"8:{_username}", mood, PresenceStatus.Online);
                _userCache[_username] = Me;
            }
            return Me;
        }

        public async Task<List<DirectMessage>> FetchContacts()
        {
            var result = new List<DirectMessage>();
            using var doc = await GetJsonAsync($"/contacts/v2/users/{Q(_username)}/contacts");
            if (doc == null || !doc.RootElement.TryGetProperty("contacts", out var contactsEl)) return result;

            foreach (var c in contactsEl.EnumerateArray())
            {
                var mri = c.GetProperty("mri").GetString();
                var displayName = c.TryGetProperty("display_name", out var dn) ? dn.GetString() : mri;
                var mood = c.TryGetProperty("profile", out var profile) && profile.TryGetProperty("mood", out var moodEl)
                    ? moodEl.GetString() : null;
                // Reuse the cached instance if there is one. Replacing it here
                // left Recents (built from FetchConversations) holding the old
                // User while presence updates mutated this new one, so Recents
                // never saw status changes.
                if (!_userCache.TryGetValue(mri, out var user))
                {
                    user = new User(displayName, StripMriPrefix(mri), mri, mood, PresenceStatus.Offline);
                    _userCache[mri] = user;
                }
                result.Add(new DirectMessage(user, 0, mri));
            }
            return result;
        }

        public async Task<List<Conversation>> FetchConversations()
        {
            var result = new List<Conversation>();
            using var doc = await GetJsonAsync("/v1/users/ME/conversations");
            if (doc == null || !doc.RootElement.TryGetProperty("conversations", out var convEl)) return result;

            foreach (var c in convEl.EnumerateArray())
            {
                var id = c.GetProperty("id").GetString();
                var unread = c.TryGetProperty("unreadMessageCount", out var uEl) ? uEl.GetInt32() : 0;
                var topic = id.StartsWith("19:")
                    ? (c.TryGetProperty("threadProperties", out var tp) && tp.TryGetProperty("topic", out var topicEl)
                        ? topicEl.GetString() : id)
                    : null;
                DateTime? lastTime = null;
                if (c.TryGetProperty("lastMessage", out var lm) && lm.TryGetProperty("originalarrivaltime", out var timeEl)
                    && DateTime.TryParse(timeEl.GetString(), out var parsed))
                    lastTime = parsed;

                if (id.StartsWith("19:"))
                {
                    // Group chat — we don't have full membership here without an extra
                    // call to /v1/threads/{id}; leave Members empty for now and let the
                    // client lazily resolve them if it needs to.
                    result.Add(new Group(topic ?? id, id, unread, Array.Empty<User>(), null, lastTime));
                }
                else
                {
                    var user = _userCache.TryGetValue(id, out var cached)
                        ? cached
                        : new User(topic ?? StripMriPrefix(id), StripMriPrefix(id), id, null, PresenceStatus.Offline);
                    _userCache[id] = user;
                    result.Add(new DirectMessage(user, unread, id, lastTime));
                }
            }
            return result;
        }

        public Task<List<Server>> FetchServers() => Task.FromResult(new List<Server>());

        public async Task<List<ConversationItem>> FetchMessages(
            Conversation conversation, Fetch fetch_type = Fetch.Newest, int message_count = 50, string identifier = null)
        {
            var result = new List<ConversationItem>();
            using var doc = await GetJsonAsync($"/v1/users/ME/conversations/{Q(conversation.Identifier)}/messages");
            if (doc == null || !doc.RootElement.TryGetProperty("messages", out var msgsEl)) return result;

            foreach (var m in msgsEl.EnumerateArray())
            {
                var messageTypeStr = m.TryGetProperty("messagetype", out var mt) ? mt.GetString() : null;
                if (messageTypeStr != null && messageTypeStr != "RichText" && messageTypeStr != "Text"
                    && messageTypeStr != "RichText/UriObject")
                    continue; // skip control/typing rows etc. — extend here for polls if you need them

                result.Add(await MessageFromJson(m));
                if (result.Count >= message_count) break;
            }
            result.Reverse(); // server returns newest-first; ConversationItem list is expected oldest-first
            return result;
        }

        private async Task<Message> MessageFromJson(JsonElement m)
        {
            var id = m.GetProperty("id").GetString();
            var fromUrl = m.TryGetProperty("from", out var fromEl) ? fromEl.GetString() : null;
            var senderMri = "8:" + (fromUrl?.Split('/').LastOrDefault()?.TrimStart('8', ':') ?? _username);
            var senderName = m.TryGetProperty("imdisplayname", out var dnEl) ? dnEl.GetString() : StripMriPrefix(senderMri);
            var author = _userCache.TryGetValue(senderMri, out var cached)
                ? cached
                : new User(senderName, StripMriPrefix(senderMri), senderMri);
            _userCache[senderMri] = author;

            var rawContent = m.TryGetProperty("content", out var contentEl) ? contentEl.GetString() : string.Empty;
            var timeStr = m.TryGetProperty("originalarrivaltime", out var timeEl) ? timeEl.GetString() : null;
            var time = DateTime.TryParse(timeStr, out var parsed) ? parsed : DateTime.UtcNow;

            var attachments = await ExtractAttachments(rawContent);
            var text = attachments != null ? null : StripSkypeMarkup(rawContent);

            return new Message(id, author, time, text, attachments);
        }

        // Parses the first <URIObject> tag (if any) into a real Attachment: images get
        // their thumbnail bytes downloaded up front so MsgByteArrayToImageConverter can
        // render them inline; files/videos/audio just get a Url so the existing
        // OpenImageCommand can download them on click (it works for any file type, not
        // just images — it just fetches bytes from Url and opens them).
        private async Task<Attachment[]> ExtractAttachments(string content)
        {
            if (string.IsNullOrEmpty(content)) return null;
            var match = System.Text.RegularExpressions.Regex.Match(
                content, "<URIObject\\b[^>]*>.*?</URIObject>", System.Text.RegularExpressions.RegexOptions.Singleline);
            if (!match.Success) return null;

            string GetAttr(string name)
            {
                var m2 = System.Text.RegularExpressions.Regex.Match(match.Value, $"\\b{name}=\"([^\"]*)\"");
                return m2.Success ? m2.Groups[1].Value : null;
            }

            var uri = GetAttr("uri");
            if (string.IsNullOrEmpty(uri)) return null;

            var type = GetAttr("type") ?? string.Empty;
            var originalNameMatch = System.Text.RegularExpressions.Regex.Match(match.Value, "<OriginalName\\s+v=\"([^\"]*)\"");
            var name = originalNameMatch.Success ? WebUnescape(originalNameMatch.Groups[1].Value) : uri.Split('/').LastOrDefault();

            bool isImage = type.StartsWith("Picture", StringComparison.OrdinalIgnoreCase);
            if (isImage)
            {
                var thumbUrl = $"{uri}/views/imgpsh";
                var fullUrl = $"{uri}/views/imgpsh_fullsize";
                byte[] thumbBytes = null;
                try { thumbBytes = await _http.GetByteArrayAsync(thumbUrl); }
                catch { /* thumbnail fetch failing shouldn't break the whole message */ }
                return new[] { new Attachment(thumbBytes, name, fullUrl, AttachmentType.Image) };
            }

            var downloadUrl = $"{uri}/views/original";
            return new[] { new Attachment(null, name, downloadUrl, AttachmentType.File) };
        }

        private static string WebUnescape(string s) =>
            s?.Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&#39;", "'");

        // Skype's RichText content is lightweight XML-ish markup (e.g. <b raw_pre="*">text</b>).
        // This does a *minimal* strip so plain text renders sanely; extend as needed for
        // mentions/quotes/cards if your theme wants to render those specially.
        private static string StripSkypeMarkup(string content)
        {
            if (string.IsNullOrEmpty(content)) return content;
            return System.Text.RegularExpressions.Regex.Replace(content, "<[^>]+>", string.Empty);
        }

        private static string StripMriPrefix(string mri) =>
            mri != null && mri.Contains(":") ? mri.Substring(mri.IndexOf(':') + 1) : mri;

        #endregion

        #region Sending / editing / deleting messages

        public async Task<bool> SendMessage(string conversation_id, string text = null, Attachment attachment = null,
            string parent_message_id = null, bool action = false)
        {
            string messageType = action ? "RichText/Contacts" : "RichText";
            string content = text;

            if (attachment != null)
            {
                var uriObject = await UploadAttachment(conversation_id, attachment);
                if (uriObject == null)
                {
                    DialogTube?.Invoke(this, new DialogBottle(DialogType.Error, $"Failed to upload {attachment.Name}."));
                    return false;
                }
                // Skype-style: media messages carry the <URIObject> tag as the whole
                // content, with any caption text as an Emotion-less trailing string.
                content = string.IsNullOrEmpty(text) ? uriObject : $"{uriObject}{text}";
                messageType = "RichText/UriObject";
            }

            var payload = new Dictionary<string, object>
            {
                ["clientmessageid"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
                ["messagetype"] = messageType,
                ["content"] = content
            };
            var resp = await PostJsonRawAsync($"/v1/users/ME/conversations/{Q(conversation_id)}/messages", payload);
            if (!resp.IsSuccessStatusCode) return false;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var msg = await MessageFromJson(doc.RootElement);
            MessageTube?.Invoke(this, new MessageRecievedBottle(conversation_id, msg, false));
            return true;
        }

        // Uploads a file via your server's two-step object flow:
        //   1) POST /v1/objects        -> create the record, get an object id
        //   2) PUT  /v1/objects/{id}/content/{kind} -> upload the raw bytes
        // Returns a <URIObject> tag ready to drop straight into message content,
        // or null on failure.
        private async Task<string> UploadAttachment(string conversation_id, Attachment attachment)
        {
            bool isImage = attachment.Type == AttachmentType.Image || attachment.Type == AttachmentType.ThumbnailImage;
            string objectType = isImage ? "pish/image" : "sharing/file";
            string contentKind = isImage ? "imgpsh" : "original";
            string viewName = isImage ? "imgpsh" : "original";

            var createResp = await PostJsonRawAsync("/v1/objects", new
            {
                type = objectType,
                filename = attachment.Name,
                permissions = new Dictionary<string, string[]> { [conversation_id] = new[] { "read" } }
            });
            if (!createResp.IsSuccessStatusCode) return null;

            string objectId;
            using (var doc = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync()))
                objectId = doc.RootElement.GetProperty("id").GetString();

            var bytes = attachment.File;
            if (bytes == null)
            {
                // Attachment was constructed from a URL rather than raw bytes — nothing
                // to upload; caller should fetch the bytes first if that's the case.
                return null;
            }

            var byteContent = new ByteArrayContent(bytes);
            byteContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            var putResp = await _http.PutAsync($"/v1/objects/{Q(objectId)}/content/{contentKind}", byteContent);
            if (!putResp.IsSuccessStatusCode) return null;

            var origin = BaseUrl;
            var objectUrl = $"{origin}/v1/objects/{objectId}";
            var viewUrl = $"{objectUrl}/views/{viewName}";

            return isImage
                ? $"<URIObject type=\"Picture.1\" uri=\"{objectUrl}\" url_thumbnail=\"{viewUrl}\"><a href=\"{viewUrl}\">{viewUrl}</a></URIObject>"
                : $"<URIObject type=\"File.1\" uri=\"{objectUrl}\" url_thumbnail=\"{viewUrl}\"><OriginalName v=\"{System.Security.SecurityElement.Escape(attachment.Name)}\"/><a href=\"{viewUrl}\">{viewUrl}</a></URIObject>";
        }

        public async Task<bool> EditMessage(string conversation_id, string message_id, string new_text)
        {
            var resp = await _http.PutAsync(
                $"/v1/users/ME/conversations/{Q(conversation_id)}/messages/{Q(message_id)}",
                new StringContent(JsonSerializer.Serialize(new { content = new_text }), Encoding.UTF8, "application/json"));
            if (!resp.IsSuccessStatusCode) return false;
            MessageTube?.Invoke(this, new MessageEditedBottle(conversation_id, message_id,
                new Message(message_id, Me, DateTime.UtcNow, new_text)));
            return true;
        }

        public async Task<bool> DeleteMessage(string conversation_id, string message_id)
        {
            var resp = await _http.DeleteAsync($"/v1/users/ME/conversations/{Q(conversation_id)}/messages/{Q(message_id)}");
            if (!resp.IsSuccessStatusCode) return false;
            MessageTube?.Invoke(this, new MessageDeletedBottle(conversation_id, message_id));
            return true;
        }

        #endregion

        #region Presence / typing

        public async Task<bool> SetConnectionStatus(PresenceStatus status)
        {
            var skypeStatus = status switch
            {
                PresenceStatus.Online => "Online",
                PresenceStatus.Away => "Away",
                PresenceStatus.DoNotDisturb => "DoNotDisturb",
                PresenceStatus.Invisible => "Hidden",
                PresenceStatus.Offline => "Offline",
                _ => "Online"
            };
            var resp = await PutJsonAsync("/v1/users/ME/presenceDocs/messagingService", new { status = skypeStatus });
            if (Me != null) Me.ConnectionStatus = status;
            return resp != null;
        }

        public Task<bool> SetMood(string status)
        {
            // No dedicated mood endpoint is exposed in Server/server.js today; the
            // profile route is read-only. Add a PUT /profile/... route server-side
            // if you want mood changes to persist.
            DialogTube?.Invoke(this, new DialogBottle(DialogType.Warning, "Setting mood isn't supported by the server yet."));
            return Task.FromResult(false);
        }

        public async Task<bool> SetTyping(string identifier, bool typing)
        {
            var resp = await PostJsonRawAsync($"/v1/users/ME/conversations/{Q(identifier)}/messages", new
            {
                messagetype = typing ? "Control/Typing" : "Control/ClearTyping",
                content = string.Empty
            });
            return resp.IsSuccessStatusCode;
        }

        #endregion

        #region Long-poll event loop

        private async Task PollLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var doc = await PostJsonAsync(
                        $"/v1/users/ME/endpoints/SELF/subscriptions/{_subscriptionId}/poll", new { });
                    if (doc != null && doc.RootElement.TryGetProperty("eventMessages", out var events))
                    {
                        foreach (var evt in events.EnumerateArray())
                            await HandleEvent(evt);
                    }
                }
                catch (Exception ex)
                {
                    // Transient network hiccups shouldn't kill the loop — log and back off.
                    DialogTube?.Invoke(this, new DialogBottle(DialogType.Warning, $"Poll error: {ex.Message}"));
                    await Task.Delay(3000, ct).ContinueWith(_ => { });
                }
            }
        }

        private async Task HandleEvent(JsonElement evt)
        {
            if (!evt.TryGetProperty("resourceType", out var typeEl)) return;
            var type = typeEl.GetString();
            System.Diagnostics.Debug.WriteLine($"[NSkype] HandleEvent: resourceType={type}");
            if (!evt.TryGetProperty("resource", out var resource)) return;

            switch (type)
            {
                case "NewMessage":
                    {
                        if (!resource.TryGetProperty("messagetype", out var mt)) return;
                        var mtStr = mt.GetString();
                        if (mtStr == "Control/Typing" || mtStr == "Control/ClearTyping" || mtStr == "Signal/Call")
                        {
                            // Signal/Call is a legacy-style call notification kept for
                            // compatibility with older clients — we use the cleaner
                            // CallNotification/CallAcceptance/CallEnd events below instead,
                            // so just skip it here rather than showing it as a chat message.
                            return;
                        }
                        var convId = resource.TryGetProperty("conversationLink", out var cl)
                            ? cl.GetString()?.Split('/').LastOrDefault()
                            : null;
                        var msg = await MessageFromJson(resource);
                        MessageTube?.Invoke(this, new MessageRecievedBottle(convId ?? msg.Identifier, msg, false));
                        break;
                    }
                case "UserPresence":
                    {
                        // selfLink is present on every presence document, ours and
                        // every contact's alike (see userPresenceResource in
                        // server.js) - its mere presence was never a valid way to
                        // detect a self-echo, which is why this unconditionally
                        // did nothing before regardless of whose presence this
                        // actually was. What DOES tell us whose presence this is,
                        // is the mri embedded in selfLink's own URL:
                        // .../v1/users/{mri}/presenceDocs/messagingService.
                        System.Diagnostics.Debug.WriteLine($"[NSkype] UserPresence raw resource: {resource.GetRawText()}");
                        if (!resource.TryGetProperty("selfLink", out var selfLinkEl))
                        {
                            System.Diagnostics.Debug.WriteLine("[NSkype] UserPresence: no selfLink property found, bailing");
                            return;
                        }
                        var mriMatch = System.Text.RegularExpressions.Regex.Match(
                            selfLinkEl.GetString() ?? "", @"/v1/users/([^/]+)/presenceDocs/");
                        if (!mriMatch.Success)
                        {
                            System.Diagnostics.Debug.WriteLine($"[NSkype] UserPresence: selfLink didn't match expected shape: {selfLinkEl.GetString()}");
                            return;
                        }
                        var presenceMri = Uri.UnescapeDataString(mriMatch.Groups[1].Value);
                        var presenceUsername = StripMriPrefix(presenceMri);
                        System.Diagnostics.Debug.WriteLine($"[NSkype] UserPresence: parsed mri={presenceMri} username={presenceUsername} (our own username={_username})");

                        if (string.Equals(presenceUsername, _username, StringComparison.OrdinalIgnoreCase))
                        {
                            System.Diagnostics.Debug.WriteLine("[NSkype] UserPresence: this is our own presence echoing back, skipping");
                            return; // genuinely our own presence echoing back - nothing to update
                        }

                        if (!resource.TryGetProperty("availability", out var availEl)
                            && !resource.TryGetProperty("status", out availEl))
                        {
                            System.Diagnostics.Debug.WriteLine("[NSkype] UserPresence: no availability/status property found, bailing");
                            return;
                        }
                        var rawStatus = availEl.GetString();
                        var newStatus = rawStatus switch
                        {
                            "Online" => PresenceStatus.Online,
                            "Away" => PresenceStatus.Away,
                            "DoNotDisturb" => PresenceStatus.DoNotDisturb,
                            "Hidden" => PresenceStatus.Invisible,
                            "Offline" => PresenceStatus.Offline,
                            _ => PresenceStatus.Offline
                        };
                        System.Diagnostics.Debug.WriteLine($"[NSkype] UserPresence: rawStatus='{rawStatus}' -> mapped to {newStatus}");

                        // _userCache is keyed by full mri for contacts (see
                        // GetContacts) - matching that convention here so this
                        // actually finds/updates the same cached instance the
                        // contacts list is already showing, rather than creating
                        // a disconnected duplicate.
                        bool foundExisting = _userCache.TryGetValue(presenceMri, out var contactUser);
                        System.Diagnostics.Debug.WriteLine($"[NSkype] UserPresence: existing cached User found for {presenceMri}? {foundExisting}. contactUser.ConnectionStatus before update: {contactUser?.ConnectionStatus}");

                        // server.js resends presence for every contact on every
                        // poll cycle, not just on real changes - without this
                        // guard we fired a UI update (and, via
                        // CompactRecentsRefreshRequested, a full Recents
                        // ItemsSource rebuild) on every poll for every contact
                        // regardless of whether anything changed. That constant
                        // rebuild tore down Recents' containers faster than the
                        // async status-icon image (Source) could ever finish
                        // loading - see SliceControl.UpdateSlices, which bails
                        // whenever Source is null. Only notify on a real change.
                        bool statusActuallyChanged = !foundExisting || contactUser.ConnectionStatus != newStatus;

                        if (!foundExisting)
                        {
                            contactUser = new User(presenceUsername, presenceUsername, presenceMri, null, newStatus);
                            _userCache[presenceMri] = contactUser;
                        }
                        contactUser.ConnectionStatus = newStatus;

                        if (!statusActuallyChanged)
                        {
                            System.Diagnostics.Debug.WriteLine($"[NSkype] UserPresence: status for {presenceMri} unchanged ({newStatus}), skipping UI notification");
                            break;
                        }

                        System.Diagnostics.Debug.WriteLine($"[NSkype] UserPresence: set contactUser.ConnectionStatus={contactUser.ConnectionStatus} for {presenceMri}, ListTube subscribed? {ListTube != null}");

                        ListTube?.Invoke(this, new ListItemUpdatedBottle(ListType.Contacts, new DirectMessage(contactUser, 0, presenceMri)));
                        break;
                    }
                case "ConversationUpdate":
                    {
                        if (resource.TryGetProperty("id", out var idEl))
                        {
                            var id = idEl.GetString();
                            var user = _userCache.TryGetValue(id, out var cached) ? cached : new User(id, StripMriPrefix(id), id);
                            ListTube?.Invoke(this, new ListItemUpdatedBottle(ListType.Conversations, new DirectMessage(user, 0, id)));
                        }
                        break;
                    }
                case "CallNotification":
                    {
                        // Someone is calling us. Cache the call id so AnswerCall
                        // can pick it up when the user accepts - and cache the
                        // caller's SDP offer too, from callNotification.mediaContent
                        // (see server.js's callIncomingPayload), since AnswerCall
                        // needs it to build a real answer.
                        var convId = resource.TryGetProperty("conversationLink", out var cl2)
                            ? cl2.GetString()?.Split('/').LastOrDefault()
                            : null;
                        string callId = null;
                        if (resource.TryGetProperty("conversationInvitation", out var invitation)
                            && invitation.TryGetProperty("conversationController", out var controllerEl))
                            callId = ExtractCallId(controllerEl.GetString());

                        if (callId == null || convId == null) return;

                        _pendingIncomingCallId = callId;
                        _pendingIncomingConvoId = convId;
                        _pendingIncomingOffer = null;
                        if (resource.TryGetProperty("callNotification", out var callNotif)
                            && callNotif.TryGetProperty("mediaContent", out var offerContent)
                            && offerContent.TryGetProperty("blob", out var offerBlob))
                            _pendingIncomingOffer = offerBlob.GetString();

                        // CallBottle.Caller must be a real Conversation (IncomingCall's
                        // XAML reads Caller.Avatar / Caller.DisplayName directly) — the
                        // (string, CallState) constructor leaves Caller null, which is
                        // exactly what was crashing IncomingCall's constructor.
                        var callerUser = _userCache.TryGetValue(convId, out var cachedCaller)
                            ? cachedCaller
                            : new User(StripMriPrefix(convId), StripMriPrefix(convId), convId);
                        var callerConvo = new DirectMessage(callerUser, 0, convId);
                        IncomingCallTube?.Invoke(this, new CallBottle(callerConvo, CallState.Ringing));
                        break;
                    }
                case "CallAcceptance":
                    {
                        // The person we called just accepted - apply their SDP
                        // answer from callAcceptance.mediaContent (see server.js's
                        // callAcceptancePayload) to actually complete the
                        // offer/answer exchange. We don't flip to CallState.Active
                        // here ourselves - CreatePeerConnectionAsync's
                        // onconnectionstatechange handler does that once ICE/DTLS
                        // genuinely finish, which is what actually starting
                        // "talking" depends on, not just receiving this event.
                        if (_activeCall == null || _peerConnection == null) return;
                        if (resource.TryGetProperty("callAcceptance", out var acceptance)
                            && acceptance.TryGetProperty("mediaContent", out var answerContent)
                            && answerContent.TryGetProperty("blob", out var answerBlob))
                        {
                            var answerSdp = answerBlob.GetString();
                            if (!string.IsNullOrEmpty(answerSdp))
                            {
                                var result = _peerConnection.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = answerSdp });
                                if (result != SetDescriptionResultEnum.OK)
                                    DialogTube?.Invoke(this, new DialogBottle(DialogType.Error, $"Couldn't apply the call's answer: {result}"));
                            }
                        }
                        break;
                    }
                case "CallEnd":
                    {
                        // The server queues a CallEnd for both parties whenever any
                        // call ends, and our own poll loop can pick up a *stale* one
                        // left over from an earlier test call if it wasn't fully
                        // drained — reacting to it unconditionally was ending brand
                        // new calls the instant a leftover event got delivered.
                        // Only react if this actually matches the call we're
                        // currently tracking.
                        var endedCallId = resource.TryGetProperty("url", out var endUrlEl) ? ExtractCallEndId(endUrlEl.GetString()) : null;
                        var currentCallId = _activeCall?.CallId ?? _pendingIncomingCallId;
                        if (endedCallId != null && currentCallId != null && endedCallId != currentCallId)
                            break; // stale event for a different/older call — ignore it

                        var convId = resource.TryGetProperty("conversationLink", out var cl3)
                            ? cl3.GetString()?.Split('/').LastOrDefault()
                            : null;
                        CleanupCall();
                        _pendingIncomingCallId = null;
                        _pendingIncomingConvoId = null;
                        CallStateChangedTube?.Invoke(this, new CallBottle(convId, CallState.Ended));
                        break;
                    }
            }
        }

        #endregion

        #region IListManagement

        public async Task<Metadata[]> FindNewContact(string query)
        {
            using var doc = await GetJsonAsync($"/v2.0/search?searchstring={Q(query)}");
            if (doc == null || !doc.RootElement.TryGetProperty("results", out var results))
                return Array.Empty<Metadata>();

            var list = new List<Metadata>();
            foreach (var r in results.EnumerateArray())
            {
                if (!r.TryGetProperty("mri", out var mriEl) && !r.TryGetProperty("id", out mriEl)) continue;
                var mri = mriEl.GetString();
                var displayName = r.TryGetProperty("display_name", out var dn) ? dn.GetString() : mri;
                list.Add(new User(displayName, StripMriPrefix(mri), mri));
            }
            return list.ToArray();
        }

        public async Task<bool> AddContact(Metadata contact, string message)
        {
            if (!(contact is User user)) return false;
            var resp = await PostJsonRawAsync($"/contacts/v2/users/{Q(_username)}/contacts", new
            {
                mri = user.Identifier,
                greeting = message ?? string.Empty
            });
            if (resp.IsSuccessStatusCode)
                ListTube?.Invoke(this, new ListItemUpdatedBottle(ListType.Contacts, new DirectMessage(user, 0, user.Identifier)));
            return resp.IsSuccessStatusCode;
        }

        #endregion

        #region ICall — real two-way audio via your server's WebSocket relay

        // Native Skype-family clients exchange WebRTC SDP through the call
        // service. The previous raw WebSocket relay had no SDP at all, which
        // meant those clients accepted the ring and then correctly tore down
        // the call during their media-answer timeout.
        private async Task<RTCPeerConnection> CreatePeerConnectionAsync()
        {
            var configuration = new RTCConfiguration { iceServers = new List<RTCIceServer>() };
            try
            {
                using var turn = await GetJsonAsync("/api/v1/turn");
                var root = turn?.RootElement;
                if (root.HasValue && root.Value.TryGetProperty("enabled", out var enabled) && enabled.GetBoolean()
                    && root.Value.TryGetProperty("urls", out var urls) && root.Value.TryGetProperty("credentials", out var credentials))
                {
                    var username = credentials.TryGetProperty("username", out var user) ? user.GetString() : null;
                    var password = credentials.TryGetProperty("password", out var pass) ? pass.GetString() : null;
                    foreach (var url in urls.EnumerateArray())
                        configuration.iceServers.Add(new RTCIceServer { urls = url.GetString(), username = username, credential = password });
                }
            }
            catch { /* Host candidates still permit same-network calls. */ }

            var peer = new RTCPeerConnection(configuration);
            peer.addTrack(new MediaStreamTrack(new List<AudioFormat>
            {
                new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMU),
                new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMA)
            }, MediaStreamStatusEnum.SendRecv));
            peer.onconnectionstatechange += state =>
            {
                if (state == RTCPeerConnectionState.connected && !_mediaConnected)
                {
                    _mediaConnected = true;
                    StartAudioDevices();
                    if (_activeCall != null)
                    {
                        _activeCall.State = CallState.Active;
                        CallStateChangedTube?.Invoke(this, new CallBottle(_activeCall.ConversationId, CallState.Active));
                    }
                }
                else if (state == RTCPeerConnectionState.failed || state == RTCPeerConnectionState.closed)
                {
                    if (_activeCall != null) CallStateChangedTube?.Invoke(this, new CallBottle(_activeCall.ConversationId, CallState.Ended));
                    CleanupCall();
                }
            };
            peer.OnRtpPacketReceived += (remote, media, packet) =>
            {
                if (media != SDPMediaTypesEnum.audio || packet?.Payload == null) return;
                var pcm = packet.Header.PayloadType == 8 ? DecodeALaw(packet.Payload) : DecodeMuLaw(packet.Payload);
                _playbackBuffer?.AddSamples(pcm, 0, pcm.Length);
            };
            return peer;
        }

        private static byte[] DecodeMuLaw(byte[] source)
        {
            var pcm = new byte[source.Length * 2];
            for (var i = 0; i < source.Length; i++)
            {
                var value = ~source[i];
                var sample = ((value & 0x0f) << 3) + 0x84;
                sample <<= (value & 0x70) >> 4;
                sample = (value & 0x80) != 0 ? 0x84 - sample : sample - 0x84;
                pcm[i * 2] = (byte)sample;
                pcm[i * 2 + 1] = (byte)(sample >> 8);
            }
            return pcm;
        }

        // Standard G.711 mu-law encoder — mirrors DecodeMuLaw below it.
        private static byte[] EncodeMuLaw(byte[] pcm16)
        {
            const int bias = 0x84, clip = 32635;
            var encoded = new byte[pcm16.Length / 2];
            for (int i = 0, o = 0; i < pcm16.Length - 1; i += 2, o++)
            {
                int sample = (short)((pcm16[i + 1] << 8) | pcm16[i]);
                int sign = (sample >> 8) & 0x80;
                if (sign != 0) sample = -sample;
                if (sample > clip) sample = clip;
                sample += bias;
                int exponent = 7;
                for (int mask = 0x4000; (sample & mask) == 0 && exponent > 0; mask >>= 1) exponent--;
                int mantissa = (sample >> (exponent + 3)) & 0x0f;
                encoded[o] = (byte)~(sign | (exponent << 4) | mantissa);
            }
            return encoded;
        }

        private static byte[] DecodeALaw(byte[] source)
        {
            var pcm = new byte[source.Length * 2];
            for (var i = 0; i < source.Length; i++)
            {
                var value = source[i] ^ 0x55;
                var sample = (value & 0x0f) << 4;
                var exponent = (value & 0x70) >> 4;
                sample = exponent == 0 ? sample + 8 : exponent == 1 ? sample + 0x108 : (sample + 0x108) << (exponent - 1);
                if ((value & 0x80) == 0) sample = -sample;
                pcm[i * 2] = (byte)sample;
                pcm[i * 2 + 1] = (byte)(sample >> 8);
            }
            return pcm;
        }

        // PCMU/PCMA (G.711) are fixed-rate codecs — 8000Hz is mandatory,
        // not a quality tradeoff. Capturing at any other rate would
        // desync playback pitch/speed on whichever end decodes it.
        private void StartAudioDevices()
        {
            const int sampleRate = 8000;
            var format = new NAudio.Wave.WaveFormat(sampleRate, 16, 1);

            _waveIn = new NAudio.Wave.WaveInEvent { WaveFormat = format, BufferMilliseconds = 20 };
            _waveIn.DataAvailable += WaveIn_DataAvailable;
            _waveIn.StartRecording();

            _playbackBuffer = new NAudio.Wave.BufferedWaveProvider(format) { DiscardOnBufferOverflow = true };
            _waveOut = new NAudio.Wave.WaveOutEvent();
            _waveOut.Init(_playbackBuffer);
            _waveOut.Play();
        }

        private void WaveIn_DataAvailable(object sender, NAudio.Wave.WaveInEventArgs e)
        {
            if (_muted || _peerConnection == null || !_mediaConnected) return;
            var data = new byte[e.BytesRecorded];
            Buffer.BlockCopy(e.Buffer, 0, data, 0, e.BytesRecorded);
            // Encode to mu-law and hand off to the peer connection's RTP
            // sender. durationRtpUnits is the sample count of this frame at
            // the 8000Hz clock G.711 uses (== encoded byte count, since
            // mu-law is 1 byte/sample), matching what SendAudio expects.
            var encoded = EncodeMuLaw(data);
            try { _peerConnection.SendAudio((uint)encoded.Length, encoded); }
            catch { /* connection likely tearing down; CleanupCall handles that */ }
        }

        // This signaling API has no separate ICE-candidate exchange endpoint —
        // server.js's mediaContent is a single SDP blob per offer/answer, sent
        // once. That means non-trickle ICE: we must wait for gathering to
        // finish so all candidates are already embedded in the SDP before
        // it's POSTed, rather than trickling them in afterward.
        private static Task WaitForIceGatheringComplete(RTCPeerConnection pc)
        {
            if (pc.iceGatheringState == RTCIceGatheringState.complete) return Task.CompletedTask;
            var tcs = new TaskCompletionSource<bool>();
            void handler(RTCIceGatheringState state)
            {
                if (state != RTCIceGatheringState.complete) return;
                pc.onicegatheringstatechange -= handler;
                tcs.TrySetResult(true);
            }
            pc.onicegatheringstatechange += handler;
            return tcs.Task;
        }

        public bool SupportsVideoCalls => false; // flip once a video capture/render pipeline is added

        public event EventHandler<CallBottle> IncomingCallTube;
        public event EventHandler<CallBottle> CallStateChangedTube;

        public async Task<ActiveCall> StartCall(string convo_id, bool is_video_call, bool start_muted)
        {
            if (is_video_call)
                DialogTube?.Invoke(this, new DialogBottle(DialogType.Warning,
                    "Video calling isn't implemented yet — starting an audio-only call instead."));

            _muted = start_muted;

            _peerConnection = await CreatePeerConnectionAsync();
            var offer = _peerConnection.createOffer(null);
            await _peerConnection.setLocalDescription(offer);
            await WaitForIceGatheringComplete(_peerConnection);
            // localDescription's SDP reflects the full gathered set of
            // candidates - offer.sdp above would only have whatever was
            // gathered by the time createOffer returned, likely incomplete.
            var offerSdp = _peerConnection.localDescription.sdp.ToString();

            // The caller's offer goes in callInvitation.mediaContent - this is
            // what callIncomingPayload() on the server surfaces to the callee
            // as callNotification.mediaContent, and what the callee's
            // AnswerCall applies as the remote offer.
            var resp = await PostJsonRawAsync($"/v1/users/ME/conversations/{Q(convo_id)}/calls", new
            {
                callModalities = new[] { "Audio" },
                callInvitation = new { mediaContent = new { type = "sdp", blob = offerSdp } }
            });
            if (!resp.IsSuccessStatusCode)
            {
                DialogTube?.Invoke(this, new DialogBottle(DialogType.Error, "Failed to start the call."));
                CleanupCall();
                return null;
            }

            var callId = ExtractCallId(resp.Headers.Location?.ToString());
            var respText = await resp.Content.ReadAsStringAsync();
            if (callId == null) callId = ExtractCallId(respText);
            if (callId == null)
            {
                DialogTube?.Invoke(this, new DialogBottle(DialogType.Error, "Call was created but no call id came back."));
                CleanupCall();
                return null;
            }

            _activeCall = new ActiveCall(callId, convo_id, false, Array.Empty<User>());
            // The remote answer arrives via the CallAcceptance event (see
            // HandleEvent) - that's what actually gets applied to the peer
            // connection and lets media start flowing. Nothing more to do
            // here; Skymu's own UI shows "ringing" as soon as this returns
            // non-null.
            return _activeCall;
        }

        public async Task<ActiveCall> AnswerCall(string convo_id)
        {
            if (_pendingIncomingCallId == null || _pendingIncomingConvoId != convo_id)
            {
                DialogTube?.Invoke(this, new DialogBottle(DialogType.Error, "No incoming call to answer for this conversation."));
                return null;
            }

            var callId = _pendingIncomingCallId;
            var offerSdp = _pendingIncomingOffer;
            _pendingIncomingCallId = null;
            _pendingIncomingConvoId = null;
            _pendingIncomingOffer = null;

            if (string.IsNullOrEmpty(offerSdp))
            {
                DialogTube?.Invoke(this, new DialogBottle(DialogType.Error, "No offer was received for this call — can't answer it."));
                return null;
            }

            _peerConnection = await CreatePeerConnectionAsync();
            // Track the call from the start of answering, not after the accept
            // POST returns. Otherwise a CallEnd arriving during ICE gathering
            // or the POST finds no active call, cleans up, and then this
            // method resurrects a dead call as "connected" afterwards.
            _activeCall = new ActiveCall(callId, convo_id, false, Array.Empty<User>());
            var setOfferResult = _peerConnection.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = offerSdp });
            if (setOfferResult != SetDescriptionResultEnum.OK)
            {
                DialogTube?.Invoke(this, new DialogBottle(DialogType.Error, $"Couldn't apply the incoming call's offer: {setOfferResult}"));
                CleanupCall();
                return null;
            }
            var answer = _peerConnection.createAnswer(null);
            await _peerConnection.setLocalDescription(answer);
            await WaitForIceGatheringComplete(_peerConnection);
            var answerSdp = _peerConnection.localDescription.sdp.ToString();

            var resp = await PostJsonRawAsync($"/v1/calls/{Q(callId)}/accept", new
            {
                callAcceptance = new
                {
                    acceptedCallModalities = new[] { "Audio" },
                    mediaContent = new { type = "sdp", blob = answerSdp }
                }
            });
            if (!resp.IsSuccessStatusCode)
            {
                DialogTube?.Invoke(this, new DialogBottle(DialogType.Error, "Failed to answer the call."));
                CleanupCall();
                return null;
            }

            // Don't flip to CallState.Active here - unlike the old relay
            // (which was "connected" the instant the socket opened), real
            // media only starts flowing once ICE/DTLS actually finish. That
            // transition is handled by CreatePeerConnectionAsync's
            // onconnectionstatechange handler, which fires the state-changed
            // event itself once it genuinely happens.
            // CleanupCall nulls _activeCall if the remote ended the call while
            // we were answering - don't report a dead call as answered.
            if (_activeCall == null) return null;
            return _activeCall;
        }

        public async Task<bool> DeclineCall(string convo_id)
        {
            if (_pendingIncomingCallId == null || _pendingIncomingConvoId != convo_id) return false;
            var callId = _pendingIncomingCallId;
            _pendingIncomingCallId = null;
            _pendingIncomingConvoId = null;

            var resp = await _http.DeleteAsync($"/v1/calls/{Q(callId)}");
            return resp.IsSuccessStatusCode;
        }

        public async Task<bool> EndCall(ActiveCall call)
        {
            var resp = await _http.DeleteAsync($"/v1/calls/{Q(call.CallId)}");
            CleanupCall();
            CallStateChangedTube?.Invoke(this, new CallBottle(call.ConversationId, CallState.Ended));
            return resp.IsSuccessStatusCode;
        }

        public Task<bool> SetMuted(ActiveCall call, bool muted)
        {
            _muted = muted; // gates whether captured mic frames actually get sent
            return Task.FromResult(true);
        }

        public Task<bool> SetVideoEnabled(ActiveCall call, bool enabled)
        {
            DialogTube?.Invoke(this, new DialogBottle(DialogType.Warning, "Video calling isn't implemented yet."));
            return Task.FromResult(false);
        }

        private void CleanupCall()
        {
            try { _peerConnection?.close(); } catch { /* already closed */ }
            _peerConnection = null;
            _mediaConnected = false;
            _pendingIncomingOffer = null;

            try { _waveIn?.StopRecording(); _waveIn?.Dispose(); } catch { /* already stopped */ }
            try { _waveOut?.Stop(); _waveOut?.Dispose(); } catch { /* already stopped */ }
            _waveIn = null;
            _waveOut = null;
            _playbackBuffer = null;

            _activeCall = null;
            _muted = false;
        }

        private static string ExtractCallId(string urlOrJson)
        {
            if (string.IsNullOrEmpty(urlOrJson)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(urlOrJson, "/v1/calls/([^/\"]+)");
            return m.Success ? Uri.UnescapeDataString(m.Groups[1].Value) : null;
        }

        // CallEnd events carry the call id in a different URL shape:
        // .../callAgent/{id}/call/end/ — separate from ExtractCallId's
        // /v1/calls/{id} shape used for the initial call-creation response.
        private static string ExtractCallEndId(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(url, "/callAgent/([^/]+)/call/end/?");
            return m.Success ? Uri.UnescapeDataString(m.Groups[1].Value) : null;
        }

        #endregion
    }
}
