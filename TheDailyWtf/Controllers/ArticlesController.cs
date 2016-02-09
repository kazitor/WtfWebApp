﻿using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Web;
using System.Web.Mvc;
using System.Web.Security;
using TheDailyWtf.Data;
using TheDailyWtf.Discourse;
using TheDailyWtf.Legacy;
using TheDailyWtf.Models;
using TheDailyWtf.ViewModels;

namespace TheDailyWtf.Controllers
{
    public class ArticlesController : WtfControllerBase
    {
        public static readonly TimeSpan CommentEditTimeout = TimeSpan.FromDays(2);

        //
        // GET: /Articles/

        [OutputCache(CacheProfile = CacheProfile.Timed1Minute)]
        public ActionResult Index()
        {
            return View(new ArticlesIndexViewModel());
        }

        public ActionResult ViewArticleById(int id)
        {
            var article = ArticleModel.GetArticleById(id);
            if (article == null)
                return HttpNotFound();

            return Redirect(article.Url);
        }

        [OutputCache(CacheProfile = CacheProfile.Timed1Minute)]
        public ActionResult ViewArticle(string articleSlug)
        {
            var article = ArticleModel.GetArticleBySlug(articleSlug);
            if (article == null)
                return HttpNotFound();

            return View(new ViewArticleViewModel(article));
        }

        [OutputCache(CacheProfile = CacheProfile.Timed1Minute)]
        public ActionResult ViewArticleComments(string articleSlug, int page)
        {
            var article = ArticleModel.GetArticleBySlug(articleSlug);
            if (article == null)
                return HttpNotFound();

            return View(new ViewCommentsViewModel(article, page));
        }

        public ActionResult Login()
        {
            return View(new CommentsLoginViewModel());
        }

        class LoginError
        {
            [JsonProperty(PropertyName = "message")]
            public string Message { get; set; }
        }

        class LoginSuccess
        {
            [JsonProperty(PropertyName = "username")]
            public string Name { get; set; }
            [JsonProperty(PropertyName = "userslug")]
            public string Slug { get; set; }
        }

        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public ActionResult Login(string username, string password)
        {
            using (var client = new HttpClient())
            {
                using (var response = client.PostAsync("https://" + Config.NodeBB.Host + "/api/ns/login", new FormUrlEncodedContent(
                    new Dictionary<string, string> { { "username", username }, { "password", password } })).Result)
                {
                    if (response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        var err = JsonConvert.DeserializeObject<LoginError>(response.Content.ReadAsStringAsync().Result);
                        ModelState.AddModelError(string.Empty, err.Message);
                        return View(new CommentsLoginViewModel());
                    }
                    response.EnsureSuccessStatusCode();
                    var user = JsonConvert.DeserializeObject<LoginSuccess>(response.Content.ReadAsStringAsync().Result);

                    var issued = DateTime.Now;
                    var expiration = issued.AddYears(2);
                    var ticket = new FormsAuthenticationTicket(1, user.Name, issued, expiration, true, string.Format("nodebb:{0}", user.Slug));
                    Response.SetCookie(new HttpCookie("tdwtf_token", FormsAuthentication.Encrypt(ticket))
                    {
                        HttpOnly = true,
                        Expires = expiration,
                        Path = FormsAuthentication.FormsCookiePath,
                    });
                    Response.SetCookie(new HttpCookie("tdwtf_token_name", user.Name)
                    {
                        HttpOnly = false,
                        Expires = expiration,
                        Path = FormsAuthentication.FormsCookiePath,
                    });
                }
            }
            return Redirect("/");
        }

        // not cached
        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public ActionResult ViewArticleComments(string articleSlug, int page, CommentFormModel form)
        {
            var article = ArticleModel.GetArticleBySlug(articleSlug);
            if (article == null)
                return HttpNotFound();

            string token = null;
            var cookie = Request.Cookies["tdwtf_token"];
            if (cookie != null)
            {
                var ticket = FormsAuthentication.Decrypt(cookie.Value);
                if (!ticket.Expired)
                {
                    form.Name = ticket.Name;
                    token = ticket.UserData;
                }
            }

            if (token == null)
            {
                CheckRecaptcha();
            }

            if (string.IsNullOrWhiteSpace(form.Name))
                ModelState.AddModelError(string.Empty, "A name is required.");
            if (string.IsNullOrWhiteSpace(form.Body))
                ModelState.AddModelError(string.Empty, "A comment is required.");
            if (form.Parent != null && !CommentModel.FromArticle(article).Any(c => c.Id == form.Parent))
                ModelState.AddModelError(string.Empty, "Invalid parent comment.");
            if (ModelState.IsValid)
            {
                int commentId = StoredProcs.Comments_CreateOrUpdateComment(null, article.Id, form.Body, form.Name, DateTime.Now, Request.ServerVariables["REMOTE_ADDR"], token, form.Parent).Execute().Value;
                return Redirect(string.Format("{0}/{1}#comment-{2}", article.CommentsUrl, article.CachedCommentCount / ViewCommentsViewModel.CommentsPerPage + 1, commentId));
            }

            return View(new ViewCommentsViewModel(article, page) { Comment = form });
        }

        public ActionResult Addendum(string articleSlug, int id)
        {
            var article = ArticleModel.GetArticleBySlug(articleSlug);
            if (article == null)
                return HttpNotFound();

            // TODO: get comment by ID would be much more efficient here
            var comments = CommentModel.FromArticle(article);
            if (!comments.Any(c => c.Id == id))
                return HttpNotFound();

            var comment = comments.First(c => c.Id == id);
            if (comment.UserToken == null || comment.PublishedDate.Add(CommentEditTimeout) <= DateTime.Now)
                return new HttpStatusCodeResult(HttpStatusCode.Forbidden);

            var cookie = Request.Cookies["tdwtf_token"];
            if (cookie == null)
                return new HttpStatusCodeResult(HttpStatusCode.Forbidden);

            var ticket = FormsAuthentication.Decrypt(cookie.Value);
            if (ticket.Expired || comment.UserToken != ticket.UserData)
                return new HttpStatusCodeResult(HttpStatusCode.Forbidden);

            return View(new AddendumViewModel(article, comment));
        }

        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public ActionResult Addendum(string articleSlug, int id, CommentFormModel post)
        {
            var article = ArticleModel.GetArticleBySlug(articleSlug);
            if (article == null)
                return HttpNotFound();

            if (string.IsNullOrWhiteSpace(post.Body))
                return Redirect(article.Url);

            // TODO: get comment by ID would be much more efficient here
            var comments = CommentModel.FromArticle(article);
            if (!comments.Any(c => c.Id == id))
                return HttpNotFound();

            var comment = comments.First(c => c.Id == id);
            if (comment.UserToken == null || comment.PublishedDate.Add(CommentEditTimeout) <= DateTime.Now)
                return new HttpStatusCodeResult(HttpStatusCode.Forbidden);

            var cookie = Request.Cookies["tdwtf_token"];
            if (cookie == null)
                return new HttpStatusCodeResult(HttpStatusCode.Forbidden);

            var ticket = FormsAuthentication.Decrypt(cookie.Value);
            if (ticket.Expired || comment.UserToken != ticket.UserData)
                return new HttpStatusCodeResult(HttpStatusCode.Forbidden);

            StoredProcs.Comments_CreateOrUpdateComment(comment.Id, article.Id, string.Format("{0}\n\n**Addendum {1}:**\n{2}", comment.BodyRaw, DateTime.Now, post.Body),
                comment.Username, comment.PublishedDate, comment.UserIP, comment.UserToken, comment.ParentCommentId).ExecuteNonQuery();

            return Redirect(article.Url);
        }

        public ActionResult ViewLegacyArticle(string articleSlug)
        {
            return RedirectToActionPermanent("ViewArticle", new { articleSlug });
        }

        public ActionResult ViewLegacyPost(int? postId)
        {
            if (postId == null)
                return HttpNotFound();

            var article = ArticleModel.GetArticleByLegacyPost((int)postId);
            if (article == null)
                return HttpNotFound();

            return RedirectToActionPermanent("ViewArticle", new { articleSlug = article.Slug });
        }

        public ActionResult ViewLegacyArticleComments(string articleSlug)
        {
            return RedirectToActionPermanent("ViewArticleComments", new { articleSlug });
        }

        [OutputCache(CacheProfile = CacheProfile.Timed1Minute)]
        public ActionResult ViewArticlesByMonth(int year, int month)
        {
            var date = new DateTime(year, month, 1);
            return View(Views.Articles.Index, new ArticlesIndexViewModel() { ReferenceDate = new ArticlesIndexViewModel.DateInfo(date) });
        }

        public ActionResult ViewLegacySeries(string legacySeries)
        {
            var legacyPart = LegacyEncodedUrlPart.CreateFromEncodedUrl(legacySeries);

            SeriesModel series;
            if (SeriesModel.LegacySeriesMap.TryGetValue(legacyPart.DecodedValue, out series))
                return RedirectToActionPermanent("ViewArticlesBySeries", new { seriesSlug = series.Slug });

            return HttpNotFound();
        }

        [OutputCache(CacheProfile = CacheProfile.Timed5Minutes)]
        public ActionResult ViewArticlesBySeries(string seriesSlug)
        {
            var series = SeriesModel.GetSeriesBySlug(seriesSlug);
            if (series == null)
                return HttpNotFound();

            return View(Views.Articles.Index, new ArticlesIndexViewModel() { Series = series });
        }

        [OutputCache(CacheProfile = CacheProfile.Timed5Minutes)]
        public ActionResult ViewArticlesBySeriesAndMonth(int year, int month, string seriesSlug)
        {
            var date = new DateTime(year, month, 1);
            var series = SeriesModel.GetSeriesBySlug(seriesSlug);
            if (series == null)
                return HttpNotFound();

            return View(
                Views.Articles.Index, 
                new ArticlesIndexViewModel() 
                { 
                    Series = series, 
                    ReferenceDate = new ArticlesIndexViewModel.DateInfo(date) 
                }
            );
        }

        public ActionResult RandomArticle()
        {
            var article = ArticleModel.GetRandomArticle();
            return RedirectToAction("ViewArticle", new { articleSlug = article.Slug });
        }
    }
}