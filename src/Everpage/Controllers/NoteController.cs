namespace Everpage.Controllers {
    using System;
    using System.Collections.Generic;
    using System.Dynamic;
    using System.Linq;
    using System.Text.RegularExpressions;
    using System.Web.Mvc;
    using Evernote.EDAM.NoteStore;
    using Evernote.EDAM.Type;
    using Evernote.EDAM.UserStore;
    using Thrift.Protocol;
    using Thrift.Transport;

    public class EverpageException : Exception {

        public EverpageException(string message) : base(message) { }

        public EverpageException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class NoteController : Controller {

        private const string GuidRegexPattern = "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}";
        private const string HexChars = "0123456789abcdef";

        private static readonly Regex GuidRegex = new Regex("^" + GuidRegexPattern + "$", RegexOptions.Compiled);
        private static readonly Regex LinkRegex = new Regex(@"^evernote:///view/\d+/s\d+/(" + GuidRegexPattern + @")/", RegexOptions.Compiled);
        private static readonly Regex WebAddressRegex = new Regex(@"^https://www.evernote.com/shard/s\d+/view/notebook/(" + GuidRegexPattern + ")", RegexOptions.Compiled);

        private static readonly Regex EnNoteBeginTagRegex = new Regex(@"<en-note[^>]*>", RegexOptions.Compiled);
        private static readonly Regex EnMediaTagRegex = new Regex(@"<en-media[^>]*>", RegexOptions.Compiled);
        private static readonly Regex HashAttrRegex = new Regex("hash=\"([0-9a-f]+)\"");

        private static string ParseNoteGuid(string noteId) {
            if (GuidRegex.IsMatch(noteId)) return noteId;

            var linkMatch = LinkRegex.Match(noteId);
            if (linkMatch.Success) return linkMatch.Groups[1].Value;

            var webAddressMatch = WebAddressRegex.Match(noteId);
            if (webAddressMatch.Success) return webAddressMatch.Groups[1].Value;

            throw new EverpageException("Unrecognized note identity: " + noteId);
        }

        private UserStore.Client GetUserStore() {
            var userStoreUrl = new Uri("https://www.evernote.com/edam/user");
            var userStoreTransport = new THttpClient(userStoreUrl);
            var userStoreProtocol = new TBinaryProtocol(userStoreTransport);
            return new UserStore.Client(userStoreProtocol);
        }


        private User GetUser(UserStore.Client userStore, string authToken) {
            try {
                return userStore.getUser(authToken);
            }
            catch (Exception ex) {
                throw new EverpageException(
                    String.Format("Error occurred when getting user by authentication token '{0}': {1}", authToken, ex.Message),
                    ex);
            }
        }

        private PublicUserInfo GetPublicUserInfo(UserStore.Client userStore, string username) {
            try {
                return userStore.getPublicUserInfo(username);
            }
            catch (Exception ex) {
                throw new EverpageException(
                    String.Format("Error occurred when getting public info for user '{0}': {1}", username, ex.Message),
                    ex);
            }
        }

        private NoteStore.Client GetNoteStore(UserStore.Client userStore, string authToken) {
            try {
                var noteStoreUrl = userStore.getNoteStoreUrl(authToken);
                var noteStoreTransport = new THttpClient(new Uri(noteStoreUrl));
                var noteStoreProtocol = new TBinaryProtocol(noteStoreTransport);
                return new NoteStore.Client(noteStoreProtocol);
            }
            catch (Exception ex) {
                throw new EverpageException(
                    String.Format("Error occurred when getting note store: {0}", ex.Message),
                    ex);
            }
        }

        private Note GetNote(NoteStore.Client noteStore, string authToken, string noteId) {
            try {
                return noteStore.getNote(authToken, noteId, true, false, false, false);
            }
            catch (Exception ex) {
                throw new EverpageException(
                    String.Format("Error occurred when getting note by id {0}: {1}", noteId, ex.Message),
                    ex);
            }
        }

        private static string ToHex(byte[] data) {
            var chars = new char[data.Length * 2];
            for (var i = 0; i < data.Length; i++ ) {
                var d = data[i];
                chars[i * 2] = HexChars[d / 16];
                chars[i * 2 + 1] = HexChars[d % 16];
            }

            return new String(chars);
        }

        private static string ProcessContent(PublicUserInfo userInfo, Note note) {
            var content = note.Content;

            var beginMatch = EnNoteBeginTagRegex.Match(content);
            if (!beginMatch.Success) {
                throw new EverpageException("Cannot find the begin tag <en-note> in the content.");
            }

            var startIndex = beginMatch.Index + beginMatch.Length;
            var endIndex = content.LastIndexOf("</en-note>", StringComparison.Ordinal);
            if (endIndex < 0) {
                throw new EverpageException("Cannot find the end tag </en-note> in the content.");
            }

            var body = content.Substring(startIndex, endIndex - startIndex);
            var resources = note.Resources != null
                ? note.Resources.ToDictionary(r => ToHex(r.Data.BodyHash))
                : new Dictionary<string, Resource>();

            return EnMediaTagRegex.Replace(body, m => EnMediaToImg(m, resources, userInfo.WebApiUrlPrefix));
        }

        private static string EnMediaToImg(Match match, Dictionary<string, Resource> resources, string urlPrefix) {
            var tag = match.Value;

            var hashMatch = HashAttrRegex.Match(tag);
            if (!hashMatch.Success) return tag;

            var hash = hashMatch.Groups[1].Value;

            Resource res;
            if (!resources.TryGetValue(hash, out res)) {
                return "Unknown resource (hash=" + hash + ")";
            }

            var beginTagLength = "<en-media".Length;
            return "<img" + tag.Substring(beginTagLength, hashMatch.Index - beginTagLength) +
                "src=\"" + urlPrefix + "res/" + res.Guid + "\"" + tag.Substring(hashMatch.Index + hashMatch.Length);
        }

        public ActionResult Index(string authToken, string noteId) {
            if (String.IsNullOrWhiteSpace(authToken)) {
                throw new EverpageException("Argument \"authToken\" is required.");
            }

            if (String.IsNullOrWhiteSpace(noteId)) {
                throw new EverpageException("Argument \"noteId\" is required.");
            }

            authToken = authToken.Trim();
            noteId = noteId.Trim();

            var beginTime = DateTime.Now;

            var noteGuid = ParseNoteGuid(noteId);

            var userStore = GetUserStore();
            var user = GetUser(userStore, authToken);
            var userInfo = GetPublicUserInfo(userStore, user.Username);

            var noteStore = GetNoteStore(userStore, authToken);
            var note = GetNote(noteStore, authToken, noteGuid);

            var loadTime = DateTime.Now - beginTime;

            dynamic model = new ExpandoObject();
            model.Title = note.Title;
            model.Content = ProcessContent(userInfo, note);
            model.LoadTime = loadTime;

            return View(model);
        }

        protected override void OnException(ExceptionContext filterContext) {
            filterContext.ExceptionHandled = true;

            dynamic model = new ExpandoObject();
            model.Exception = filterContext.Exception;
            filterContext.Result = View("Error", model);

            base.OnException(filterContext);
        }
    }
}