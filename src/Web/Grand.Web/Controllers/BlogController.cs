﻿using Grand.Business.Core.Interfaces.Cms;
using Grand.Business.Core.Interfaces.Common.Directory;
using Grand.Business.Core.Interfaces.Common.Localization;
using Grand.Business.Core.Interfaces.Common.Security;
using Grand.Domain.Permissions;
using Grand.Domain.Blogs;
using Grand.Domain.Customers;
using Grand.Infrastructure;
using Grand.Web.Commands.Models.Blogs;
using Grand.Web.Common.Controllers;
using Grand.Web.Common.Filters;
using Grand.Web.Events;
using Grand.Web.Features.Models.Blogs;
using Grand.Web.Models.Blogs;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Grand.SharedKernel.Attributes;

namespace Grand.Web.Controllers;

[ApiGroup(SharedKernel.Extensions.ApiConstants.ApiGroupNameV2)]
public class BlogController : BasePublicController
{
    #region Constructors

    public BlogController(
        IMediator mediator,
        IBlogService blogService,
        ITranslationService translationService,
        IContextAccessor contextAccessor,
        BlogSettings blogSettings)
    {
        _mediator = mediator;
        _blogService = blogService;
        _translationService = translationService;
        _blogSettings = blogSettings;
        _contextAccessor = contextAccessor;
    }

    #endregion

    #region Fields

    private readonly IMediator _mediator;
    private readonly IBlogService _blogService;
    private readonly ITranslationService _translationService;
    private readonly IContextAccessor _contextAccessor;
    private readonly BlogSettings _blogSettings;

    #endregion

    #region Methods

    [HttpGet]
    public virtual async Task<ActionResult<BlogPostListModel>> List(BlogPagingFilteringModel command)
    {
        if (!_blogSettings.Enabled)
            return RedirectToRoute("HomePage");

        var model = await _mediator.Send(new GetBlogPostList { Command = command });
        return View("List", model);
    }

    [HttpGet]
    public virtual async Task<ActionResult<BlogPostListModel>> BlogByTag(BlogPagingFilteringModel command)
    {
        if (!_blogSettings.Enabled)
            return RedirectToRoute("HomePage");

        var model = await _mediator.Send(new GetBlogPostList { Command = command });
        return View("List", model);
    }

    [HttpGet]
    public virtual async Task<ActionResult<BlogPostListModel>> BlogByMonth(BlogPagingFilteringModel command)
    {
        if (!_blogSettings.Enabled)
            return RedirectToRoute("HomePage");

        var model = await _mediator.Send(new GetBlogPostList { Command = command });
        return View("List", model);
    }

    [HttpGet]
    public virtual async Task<ActionResult<BlogPostListModel>> BlogByCategory(BlogPagingFilteringModel command)
    {
        if (!_blogSettings.Enabled)
            return RedirectToRoute("HomePage");

        var model = await _mediator.Send(new GetBlogPostList { Command = command });
        return View("List", model);
    }

    [HttpGet]
    public virtual async Task<ActionResult<BlogPostListModel>> BlogByKeyword(BlogPagingFilteringModel command)
    {
        if (!_blogSettings.Enabled)
            return RedirectToRoute("HomePage");

        var model = await _mediator.Send(new GetBlogPostList { Command = command });
        return View("List", model);
    }

    [HttpGet]
    public virtual async Task<ActionResult<BlogPostModel>> BlogPost(string blogPostId,
        [FromServices] IAclService aclService,
        [FromServices] IPermissionService permissionService)
    {
        if (!_blogSettings.Enabled)
            return RedirectToRoute("HomePage");

        var blogPost = await _blogService.GetBlogPostById(blogPostId);
        if (blogPost == null ||
            (blogPost.StartDateUtc.HasValue && blogPost.StartDateUtc.Value >= DateTime.UtcNow) ||
            (blogPost.EndDateUtc.HasValue && blogPost.EndDateUtc.Value <= DateTime.UtcNow))
            return RedirectToRoute("HomePage");

        //Store acl
        if (!aclService.Authorize(blogPost, _contextAccessor.StoreContext.CurrentStore.Id))
            return NotFound();

        var model = await _mediator.Send(new GetBlogPost { BlogPost = blogPost });

        //display "edit" (manage) link
        if (await permissionService.Authorize(StandardPermission.ManageAccessAdminPanel) &&
            await permissionService.Authorize(StandardPermission.ManageBlog))
            DisplayEditLink(Url.Action("Edit", "Blog", new { id = blogPost.Id, area = "Admin" }));

        return View(model);
    }

    [HttpPost]
    [AutoValidateAntiforgeryToken]
    [DenySystemAccount]
    public virtual async Task<ActionResult<AddBlogCommentModel>> BlogPost(AddBlogCommentModel model,
        [FromServices] IAclService aclService)
    {
        var blogPost = await _blogService.GetBlogPostById(model.Id);
        if (blogPost == null ||
            (blogPost.StartDateUtc.HasValue && blogPost.StartDateUtc.Value >= DateTime.UtcNow) ||
            (blogPost.EndDateUtc.HasValue && blogPost.EndDateUtc.Value <= DateTime.UtcNow))
            return Json(new {
                success = false
            });

        if (!aclService.Authorize(blogPost, _contextAccessor.StoreContext.CurrentStore.Id))
            return Json(new {
                success = false
            });

        if (ModelState.IsValid)
        {
            var blogComment = await _mediator.Send(new InsertBlogCommentCommand { Model = model, BlogPost = blogPost });

            //notification
            await _mediator.Publish(new BlogCommentEvent(blogPost, model));

            return Json(new {
                success = true,
                message = _translationService.GetResource("Blog.Comments.SuccessfullyAdded"),
                model = new {
                    blogComment.CommentText,
                    CreatedOn = HttpContext.RequestServices.GetService<IDateTimeService>()
                        .ConvertToUserTime(blogComment.CreatedOnUtc, DateTimeKind.Utc),
                    CustomerName = _contextAccessor.WorkContext.CurrentCustomer.FormatUserName(HttpContext.RequestServices
                        .GetService<CustomerSettings>().CustomerNameFormat)
                }
            });
        }

        return Json(new {
            success = false,
            message = string.Join(',', ModelState.Values.SelectMany(x => x.Errors.Select(x => x.ErrorMessage)))
        });
    }

    #endregion
}