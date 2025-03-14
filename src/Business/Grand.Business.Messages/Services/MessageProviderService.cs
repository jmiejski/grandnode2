﻿using Grand.Business.Core.Commands.Messages.Common;
using Grand.Business.Core.Extensions;
using Grand.Business.Core.Interfaces.Common.Directory;
using Grand.Business.Core.Interfaces.Common.Localization;
using Grand.Business.Core.Interfaces.Common.Stores;
using Grand.Business.Core.Interfaces.Messages;
using Grand.Business.Core.Queries.Messages;
using Grand.Business.Core.Utilities.Messages.DotLiquidDrops;
using Grand.Domain.Blogs;
using Grand.Domain.Catalog;
using Grand.Domain.Common;
using Grand.Domain.Customers;
using Grand.Domain.Knowledgebase;
using Grand.Domain.Localization;
using Grand.Domain.Messages;
using Grand.Domain.News;
using Grand.Domain.Orders;
using Grand.Domain.Shipping;
using Grand.Domain.Stores;
using Grand.Domain.Vendors;
using Grand.Infrastructure;
using Grand.SharedKernel.Extensions;
using MediatR;
using System.Net;

namespace Grand.Business.Messages.Services;

public class MessageProviderService : IMessageProviderService
{
    #region Ctor

    public MessageProviderService(
        IContextAccessor contextAccessor,
        IMessageTemplateService messageTemplateService,
        IQueuedEmailService queuedEmailService,
        ILanguageService languageService,
        IEmailAccountService emailAccountService,
        IStoreService storeService,
        IGroupService groupService,
        IMediator mediator,
        EmailAccountSettings emailAccountSettings,
        CommonSettings commonSettings)
    {
        _contextAccessor = contextAccessor;
        _messageTemplateService = messageTemplateService;
        _queuedEmailService = queuedEmailService;
        _languageService = languageService;
        _emailAccountService = emailAccountService;
        _storeService = storeService;
        _groupService = groupService;
        _emailAccountSettings = emailAccountSettings;
        _commonSettings = commonSettings;
        _mediator = mediator;
    }

    #endregion

    #region Fields
    private readonly IContextAccessor _contextAccessor;
    private readonly IMessageTemplateService _messageTemplateService;
    private readonly IQueuedEmailService _queuedEmailService;
    private readonly ILanguageService _languageService;
    private readonly IEmailAccountService _emailAccountService;
    private readonly IStoreService _storeService;
    private readonly IGroupService _groupService;
    private readonly IMediator _mediator;

    private readonly EmailAccountSettings _emailAccountSettings;
    private readonly CommonSettings _commonSettings;

    private DomainHost CurrentHost => _contextAccessor.StoreContext.CurrentHost;

    #endregion

    #region Utilities

    protected virtual async Task<Store> GetStore(string storeId)
    {
        return await _storeService.GetStoreById(storeId) ?? (await _storeService.GetAllStores()).FirstOrDefault();
    }

    protected virtual async Task<MessageTemplate> GetMessageTemplate(string messageTemplateName, string storeId)
    {
        var messageTemplate = await _messageTemplateService.GetMessageTemplateByName(messageTemplateName, storeId);

        //no template found
        if (messageTemplate == null)
            return null;

        //ensure it's active
        var isActive = messageTemplate.IsActive;
        return !isActive ? null : messageTemplate;
    }

    protected virtual async Task<EmailAccount> GetEmailAccountOfMessageTemplate(MessageTemplate messageTemplate,
        string languageId)
    {
        var emailAccounId = messageTemplate.GetTranslation(mt => mt.EmailAccountId, languageId);
        var emailAccount = (await _emailAccountService.GetEmailAccountById(emailAccounId) ??
                            await _emailAccountService.GetEmailAccountById(_emailAccountSettings
                                .DefaultEmailAccountId)) ??
                           (await _emailAccountService.GetAllEmailAccounts()).FirstOrDefault();
        return emailAccount;
    }

    protected virtual async Task<Language> EnsureLanguageIsActive(string languageId, string storeId)
    {
        //load language by specified ID
        var language = await _languageService.GetLanguageById(languageId);

        if (language is not { Published: true })
            //load any language from the specified store
            language = (await _languageService.GetAllLanguages(storeId: storeId)).FirstOrDefault();
        if (language is not { Published: true })
            //load any language
            language = (await _languageService.GetAllLanguages()).FirstOrDefault();

        if (language == null)
            throw new Exception("No active language could be loaded");
        return language;
    }

    private void AddCustomerTokensIfNotNull(LiquidObjectBuilder builder, Customer customer, Store store, Language language)
    {
        if (customer != null)
        {
            builder.AddCustomerTokens(customer, store, CurrentHost, language);
        }
    }

    #endregion

    #region Methods

    #region Customer messages

    /// <summary>
    ///     Send a message to a customer
    /// </summary>
    /// <param name="customer">Customer instance</param>
    /// <param name="store">Store</param>
    /// <param name="languageId">Message language identifier</param>
    /// <param name="templateName">Message template name</param>
    /// <param name="toEmailAccount">Send email to email account</param>
    /// <param name="customerNote">Customer note</param>
    protected virtual async Task<int> SendCustomerMessage(Customer customer, Store store, string languageId,
        string templateName, bool toEmailAccount = false, CustomerNote customerNote = null)
    {
        ArgumentNullException.ThrowIfNull(customer);

        var language = await EnsureLanguageIsActive(languageId, store.Id);

        var messageTemplate = await GetMessageTemplate(templateName, store.Id);
        if (messageTemplate == null)
            return 0;

        //email account
        var emailAccount = await GetEmailAccountOfMessageTemplate(messageTemplate, language.Id);

        var builder = new LiquidObjectBuilder(_mediator);
        builder.AddStoreTokens(store, language, emailAccount)
            .AddCustomerTokens(customer, store, CurrentHost, language, customerNote);

        var liquidObject = await builder.BuildAsync();
        //event notification
        await _mediator.MessageTokensAdded(messageTemplate, liquidObject);

        var toEmail = toEmailAccount ? emailAccount.Email : customer.Email;
        var toName = toEmailAccount ? emailAccount.DisplayName : customer.GetFullName();
        return await SendNotification(messageTemplate, emailAccount,
            languageId, liquidObject,
            toEmail, toName, reference: Reference.Customer, objectId: customer.Id);
    }

    /// <summary>
    ///     Sends 'New customer' notification message to a store owner
    /// </summary>
    /// <param name="customer">Customer instance</param>
    /// <param name="store">Store identifier</param>
    /// <param name="languageId">Message language identifier</param>
    /// <returns>Queued email identifier</returns>
    public virtual async Task<int> SendCustomerRegisteredMessage(Customer customer, Store store, string languageId)
    {
        return await SendCustomerMessage(customer, store, languageId, MessageTemplateNames.CustomerRegistered, true);
    }

    /// <summary>
    ///     Sends a welcome message to a customer
    /// </summary>
    /// <param name="customer">Customer instance</param>
    /// <param name="store">Store</param>
    /// <param name="languageId">Message language identifier</param>
    public virtual async Task<int> SendCustomerWelcomeMessage(Customer customer, Store store, string languageId)
    {
        return await SendCustomerMessage(customer, store, languageId, MessageTemplateNames.CustomerWelcome);
    }

    /// <summary>
    ///     Sends an email validation message to a customer
    /// </summary>
    /// <param name="customer">Customer instance</param>
    /// <param name="store">Store</param>
    /// <param name="languageId">Message language identifier</param>
    public virtual async Task<int> SendCustomerEmailValidationMessage(Customer customer, Store store, string languageId)
    {
        return await SendCustomerMessage(customer, store, languageId, MessageTemplateNames.CustomerEmailValidation);
    }

    /// <summary>
    ///     Sends password recovery message to a customer
    /// </summary>
    /// <param name="customer">Customer instance</param>
    /// <param name="store">Store</param>
    /// <param name="languageId">Message language identifier</param>
    public virtual async Task<int> SendCustomerPasswordRecoveryMessage(Customer customer, Store store,
        string languageId)
    {
        return await SendCustomerMessage(customer, store, languageId, MessageTemplateNames.CustomerPasswordRecovery);
    }

    /// <summary>
    ///     Sends a new customer note added notification to a customer
    /// </summary>
    /// <param name="customerNote">Customer note</param>
    /// <param name="customer">Customer</param>
    /// <param name="store">Store</param>
    /// <param name="languageId">Message language identifier</param>
    /// <returns>Queued email identifier</returns>
    public virtual async Task<int> SendNewCustomerNoteMessage(CustomerNote customerNote, Customer customer, Store store,
        string languageId)
    {
        return await SendCustomerMessage(customer, store, languageId, MessageTemplateNames.CustomerNewCustomerNote,
            customerNote: customerNote);
    }

    /// <summary>
    ///     Send an email token validation message to a customer
    /// </summary>
    /// <param name="customer">Customer instance</param>
    /// <param name="store">Store instance</param>
    /// <param name="languageId">Message language identifier</param>
    public virtual async Task<int> SendCustomerEmailTokenValidationMessage(Customer customer, Store store,
        string languageId)
    {
        return await SendCustomerMessage(customer, store, languageId,
            MessageTemplateNames.CustomerEmailTokenValidationMessage);
    }

    #endregion

    #region Order messages

    /// <summary>
    ///     Sends an order placed notification to a store owner
    /// </summary>
    /// <param name="order">Order instance</param>
    /// <param name="customer">Customer</param>
    /// <param name="languageId">Message language identifier</param>
    public virtual async Task<int> SendOrderPlacedStoreOwnerMessage(Order order, Customer customer, string languageId)
    {
        return await SendOrderStoreOwnerMessage(MessageTemplateNames.SendOrderPlacedStoreOwnerMessage, order, customer,
            languageId);
    }

    /// <summary>
    ///     Sends an order paid notification to a store owner
    /// </summary>
    /// <param name="order">Order instance</param>
    /// <param name="customer">Customer instance</param>
    /// <param name="languageId">Message language identifier</param>
    public virtual async Task<int> SendOrderPaidStoreOwnerMessage(Order order, Customer customer, string languageId)
    {
        return await SendOrderStoreOwnerMessage(MessageTemplateNames.SendOrderPaidStoreOwnerMessage, order, customer,
            languageId);
    }

    /// <summary>
    ///     Sends an order cancelled notification to an admin
    /// </summary>
    /// <param name="order">Order instance</param>
    /// <param name="customer">Customer instance</param>
    /// <param name="languageId">Message language identifier</param>
    /// <returns>Queued email identifier</returns>
    public virtual async Task<int> SendOrderCancelledStoreOwnerMessage(Order order, Customer customer,
        string languageId)
    {
        return await SendOrderStoreOwnerMessage(MessageTemplateNames.SendOrderCancelledStoreOwnerMessage, order,
            customer, languageId);
    }

    /// Sends an order refunded notification to a store owner
    /// <param name="order">Order instance</param>
    /// <param name="refundedAmount">Amount refunded</param>
    /// <param name="languageId">Message language identifier</param>
    public virtual async Task<int> SendOrderRefundedStoreOwnerMessage(Order order, double refundedAmount,
        string languageId)
    {
        var customer = await _mediator.Send(new GetCustomerByIdQuery { Id = order.CustomerId });
        return await SendOrderStoreOwnerMessage(MessageTemplateNames.SendOrderRefundedStoreOwnerMessage, order,
            customer, languageId, refundedAmount);
    }

    /// <summary>
    ///     Sends an order notification to a store owner
    /// </summary>
    /// <param name="template">Message template</param>
    /// <param name="order">Order instance</param>
    /// <param name="customer">Customer instance</param>
    /// <param name="languageId">Message language identifier</param>
    /// <param name="refundedAmount"></param>
    private async Task<int> SendOrderStoreOwnerMessage(string template, Order order, Customer customer,
        string languageId, double refundedAmount = 0)
    {
        ArgumentNullException.ThrowIfNull(order);

        var store = await GetStore(order.StoreId);
        var language = await EnsureLanguageIsActive(languageId, store.Id);

        var messageTemplate = await GetMessageTemplate(template, store.Id);
        if (messageTemplate == null)
            return 0;

        //email account
        var emailAccount = await GetEmailAccountOfMessageTemplate(messageTemplate, language.Id);

        var liquidBuilder = new LiquidObjectBuilder(_mediator);
        liquidBuilder.AddStoreTokens(store, language, emailAccount)
            .AddOrderTokens(order, customer, store, CurrentHost);
        AddCustomerTokensIfNotNull(liquidBuilder, customer, store, language);

        var liquidObject = await liquidBuilder.BuildAsync();
        //event notification
        await _mediator.MessageTokensAdded(messageTemplate, liquidObject);

        var toEmail = emailAccount.Email;
        var toName = emailAccount.DisplayName;
        return await SendNotification(messageTemplate, emailAccount,
            languageId, liquidObject,
            toEmail, toName,
            reference: Reference.Order, objectId: order.Id);
    }

    /// <summary>
    ///     Sends an order placed notification to a customer
    /// </summary>
    /// <param name="order">Order instance</param>
    /// <param name="customer"></param>
    /// <param name="languageId">Message language identifier</param>
    /// <param name="attachmentFilePath">Attachment file path</param>
    /// <param name="attachmentFileName">
    ///     Attachment file name. If specified, then this file name will be sent to a recipient.
    ///     Otherwise, "AttachmentFilePath" name will be used.
    /// </param>
    /// <param name="attachments">Attachments</param>
    public virtual async Task<int> SendOrderPlacedCustomerMessage(Order order, Customer customer, string languageId,
        string attachmentFilePath = null, string attachmentFileName = null, IEnumerable<string> attachments = null)
    {
        return await SendOrderCustomerMessage(MessageTemplateNames.SendOrderPlacedCustomerMessage, order, customer,
            languageId, attachmentFilePath, attachmentFileName, attachments);
    }

    /// <summary>
    ///     Sends an order paid notification to a customer
    /// </summary>
    /// <param name="order">Order instance</param>
    /// <param name="customer">Customer instance</param>
    /// <param name="languageId">Message language identifier</param>
    /// <param name="attachmentFilePath">Attachment file path</param>
    /// <param name="attachmentFileName">
    ///     Attachment file name. If specified, then this file name will be sent to a recipient.
    ///     Otherwise, "AttachmentFilePath" name will be used.
    /// </param>
    /// <param name="attachments">Attachments ident</param>
    public virtual async Task<int> SendOrderPaidCustomerMessage(Order order, Customer customer, string languageId,
        string attachmentFilePath = null, string attachmentFileName = null, IEnumerable<string> attachments = null)
    {
        return await SendOrderCustomerMessage(MessageTemplateNames.SendOrderPaidCustomerMessage, order, customer,
            languageId, attachmentFilePath, attachmentFileName, attachments);
    }

    /// <summary>
    ///     Sends an order completed notification to a customer
    /// </summary>
    /// <param name="order">Order instance</param>
    /// <param name="customer">Customer instance</param>
    /// <param name="languageId">Message language identifier</param>
    /// <param name="attachmentFilePath">Attachment file path</param>
    /// <param name="attachmentFileName">
    ///     Attachment file name. If specified, then this file name will be sent to a recipient.
    ///     Otherwise, "AttachmentFilePath" name will be used.
    /// </param>
    /// <param name="attachments">Attachments ident</param>
    public virtual async Task<int> SendOrderCompletedCustomerMessage(Order order, Customer customer, string languageId,
        string attachmentFilePath = null, string attachmentFileName = null, IEnumerable<string> attachments = null)
    {
        return await SendOrderCustomerMessage(MessageTemplateNames.SendOrderCompletedCustomerMessage, order, customer,
            languageId, attachmentFilePath, attachmentFileName, attachments);
    }

    /// <summary>
    ///     Sends an order cancelled notification to a customer
    /// </summary>
    /// <param name="order">Order instance</param>
    /// <param name="customer">Customer instance</param>
    /// <param name="languageId">Message language identifier</param>
    public virtual async Task<int> SendOrderCancelledCustomerMessage(Order order, Customer customer, string languageId)
    {
        return await SendOrderCustomerMessage(MessageTemplateNames.SendOrderCancelledCustomerMessage, order, customer,
            languageId);
    }

    /// <summary>
    ///     Sends an order refunded notification to a customer
    /// </summary>
    /// <param name="order">Order instance</param>
    /// <param name="refundedAmount">Amount refunded</param>
    /// <param name="languageId">Message language identifier</param>
    /// <returns>Queued email identifier</returns>
    public virtual async Task<int> SendOrderRefundedCustomerMessage(Order order, double refundedAmount,
        string languageId)
    {
        var customer = await _mediator.Send(new GetCustomerByIdQuery { Id = order.CustomerId });
        return await SendOrderCustomerMessage(MessageTemplateNames.SendOrderRefundedCustomerMessage, order, customer,
            languageId, refundedAmount: refundedAmount);
    }

    /// <summary>
    ///     Sends an order notification to a customer
    /// </summary>
    /// <param name="message">Message template</param>
    /// <param name="order">Order instance</param>
    /// <param name="customer">Customer instance</param>
    /// <param name="languageId">Message language identifier</param>
    /// <param name="attachmentFilePath">Attachment file path</param>
    /// <param name="attachmentFileName">
    ///     Attachment file name. If specified, then this file name will be sent to a recipient.
    ///     Otherwise, "AttachmentFilePath" name will be used.
    /// </param>
    /// <param name="attachments">Attachments ident</param>
    /// <param name="refundedAmount"></param>
    private async Task<int> SendOrderCustomerMessage(string message, Order order, Customer customer, string languageId,
        string attachmentFilePath = null, string attachmentFileName = null, IEnumerable<string> attachments = null,
        double refundedAmount = 0)
    {
        ArgumentNullException.ThrowIfNull(order);

        var store = await GetStore(order.StoreId);
        var language = await EnsureLanguageIsActive(languageId, store.Id);

        var messageTemplate = await GetMessageTemplate(message, store.Id);
        if (messageTemplate == null)
            return 0;

        //email account
        var emailAccount = await GetEmailAccountOfMessageTemplate(messageTemplate, language.Id);

        var builder = new LiquidObjectBuilder(_mediator);
        builder.AddStoreTokens(store, language, emailAccount)
            .AddOrderTokens(order, customer, store, CurrentHost);
        AddCustomerTokensIfNotNull(builder, customer, store, language);

        var liquidObject = await builder.BuildAsync();
        //event notification
        await _mediator.MessageTokensAdded(messageTemplate, liquidObject);

        var toEmail = order.BillingAddress.Email;
        var toName = $"{order.BillingAddress.FirstName} {order.BillingAddress.LastName}";
        return await SendNotification(messageTemplate, emailAccount,
            languageId, liquidObject,
            toEmail, toName,
            attachmentFilePath,
            attachmentFileName,
            attachments,
            reference: Reference.Order, objectId: order.Id);
    }

    /// <summary>
    ///     Sends an order placed notification to a vendor
    /// </summary>
    /// <param name="order">Order instance</param>
    /// <param name="customer">Customer instance</param>
    /// <param name="vendor">Vendor instance</param>
    /// <param name="languageId">Message language identifier</param>
    public virtual async Task<int> SendOrderPlacedVendorMessage(Order order, Customer customer, Vendor vendor,
        string languageId)
    {
        return await SendOrderVendorMessage(MessageTemplateNames.SendOrderPlacedVendorMessage, order, vendor,
            languageId);
    }

    /// <summary>
    ///     Sends an order paid notification to a vendor
    /// </summary>
    /// <param name="order">Order instance</param>
    /// <param name="vendor">Vendor instance</param>
    /// <param name="languageId">Message language identifier</param>
    public virtual async Task<int> SendOrderPaidVendorMessage(Order order, Vendor vendor, string languageId)
    {
        return await SendOrderVendorMessage(MessageTemplateNames.SendOrderPaidVendorMessage, order, vendor, languageId);
    }

    /// <summary>
    ///     Sends an order cancel notification to a vendor
    /// </summary>
    /// <param name="order">Order instance</param>
    /// <param name="vendor">Vendor instance</param>
    /// <param name="languageId">Message language identifier</param>
    public virtual async Task<int> SendOrderCancelledVendorMessage(Order order, Vendor vendor, string languageId)
    {
        return await SendOrderVendorMessage(MessageTemplateNames.SendOrderCancelledVendorMessage, order, vendor,
            languageId);
    }

    /// <summary>
    ///     Sends an order notification to a vendor
    /// </summary>
    /// <param name="message">Message template</param>
    /// <param name="order">Order instance</param>
    /// <param name="vendor">Vendor instance</param>
    /// <param name="languageId">Message language identifier</param>
    private async Task<int> SendOrderVendorMessage(string message, Order order, Vendor vendor, string languageId)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(vendor);

        var store = await GetStore(order.StoreId);
        var language = await EnsureLanguageIsActive(languageId, store.Id);

        var messageTemplate = await GetMessageTemplate(message, store.Id);
        if (messageTemplate == null)
            return 0;

        //email account
        var emailAccount = await GetEmailAccountOfMessageTemplate(messageTemplate, language.Id);

        var customer = await _mediator.Send(new GetCustomerByIdQuery { Id = order.CustomerId });

        var builder = new LiquidObjectBuilder(_mediator);
        builder.AddStoreTokens(store, language, emailAccount)
            .AddOrderTokens(order, customer, store, CurrentHost, vendor: vendor);

        AddCustomerTokensIfNotNull(builder, customer, store, language);

        var liquidObject = await builder.BuildAsync();
        //event notification
        await _mediator.MessageTokensAdded(messageTemplate, liquidObject);

        var toEmail = vendor.Email;
        var toName = vendor.Name;
        return await SendNotification(messageTemplate, emailAccount,
            languageId, liquidObject,
            toEmail, toName,
            reference: Reference.Order, objectId: order.Id);
    }


    /// <summary>
    ///     Sends a shipment sent notification to a customer
    /// </summary>
    /// <param name="shipment">Shipment</param>
    /// <param name="order">Order</param>
    public virtual async Task<int> SendShipmentSentCustomerMessage(Shipment shipment, Order order)
    {
        return await SendShipmentCustomerMessage(MessageTemplateNames.SendShipmentSentCustomerMessage, shipment, order);
    }

    /// <summary>
    ///     Sends a shipment delivered notification to a customer
    /// </summary>
    /// <param name="shipment">Shipment</param>
    /// <param name="order">Order</param>
    public virtual async Task<int> SendShipmentDeliveredCustomerMessage(Shipment shipment, Order order)
    {
        return await SendShipmentCustomerMessage(MessageTemplateNames.SendShipmentDeliveredCustomerMessage, shipment,
            order);
    }

    /// <summary>
    ///     Send a shipment notification to a customer
    /// </summary>
    /// <param name="message">Message template</param>
    /// <param name="shipment">Shipment</param>
    /// <param name="order">Order</param>
    private async Task<int> SendShipmentCustomerMessage(string message, Shipment shipment, Order order)
    {
        ArgumentNullException.ThrowIfNull(shipment);

        if (order == null)
            throw new Exception("Order cannot be loaded");

        var store = await GetStore(order.StoreId);
        var language = await EnsureLanguageIsActive(order.CustomerLanguageId, store.Id);

        var messageTemplate = await GetMessageTemplate(message, store.Id);
        if (messageTemplate == null)
            return 0;

        //email account
        var emailAccount = await GetEmailAccountOfMessageTemplate(messageTemplate, language.Id);

        var customer = await _mediator.Send(new GetCustomerByIdQuery { Id = order.CustomerId });

        var builder = new LiquidObjectBuilder(_mediator);
        builder.AddStoreTokens(store, language, emailAccount)
            .AddShipmentTokens(shipment, order, store, CurrentHost, language)
            .AddOrderTokens(order, customer, store, CurrentHost);

        AddCustomerTokensIfNotNull(builder, customer, store, language);

        var liquidObject = await builder.BuildAsync();
        //event notification
        await _mediator.MessageTokensAdded(messageTemplate, liquidObject);

        var toEmail = order.BillingAddress.Email;
        var toName = $"{order.BillingAddress.FirstName} {order.BillingAddress.LastName}";
        return await SendNotification(messageTemplate, emailAccount,
            language.Id, liquidObject,
            toEmail, toName,
            reference: Reference.Shipment, objectId: shipment.Id);
    }


    /// <summary>
    ///     Sends a new order note added notification to a customer
    /// </summary>
    /// <param name="order">Order</param>
    /// <param name="orderNote">Order note</param>
    public virtual async Task<int> SendNewOrderNoteAddedCustomerMessage(Order order, OrderNote orderNote)
    {
        ArgumentNullException.ThrowIfNull(orderNote);
        ArgumentNullException.ThrowIfNull(order);

        var store = await GetStore(order.StoreId);
        var language = await EnsureLanguageIsActive(order.CustomerLanguageId, store.Id);

        var messageTemplate =
            await GetMessageTemplate(MessageTemplateNames.SendNewOrderNoteAddedCustomerMessage, store.Id);
        if (messageTemplate == null)
            return 0;

        //email account
        var emailAccount = await GetEmailAccountOfMessageTemplate(messageTemplate, language.Id);

        var customer = await _mediator.Send(new GetCustomerByIdQuery { Id = order.CustomerId });

        var builder = new LiquidObjectBuilder(_mediator);
        builder.AddStoreTokens(store, language, emailAccount)
            .AddOrderTokens(order, customer, store, CurrentHost, orderNote);

        AddCustomerTokensIfNotNull(builder, customer, store, language);

        var liquidObject = await builder.BuildAsync();
        //event notification
        await _mediator.MessageTokensAdded(messageTemplate, liquidObject);

        var toEmail = order.BillingAddress.Email;
        var toName = $"{order.BillingAddress.FirstName} {order.BillingAddress.LastName}";
        return await SendNotification(messageTemplate, emailAccount,
            language.Id, liquidObject,
            toEmail, toName,
            reference: Reference.Order, objectId: order.Id);
    }

    #endregion

    #region Newsletter messages

    /// <summary>
    ///     Sends a newsletter subscription activation message
    /// </summary>
    /// <param name="subscription">Newsletter subscription</param>
    /// <param name="languageId">Language identifier</param>
    public virtual async Task<int> SendNewsLetterSubscriptionActivationMessage(NewsLetterSubscription subscription,
        string languageId)
    {
        return await SendNewsLetterSubscriptionMessage(MessageTemplateNames.SendNewsLetterSubscriptionActivationMessage,
            subscription, languageId);
    }

    /// <summary>
    ///     Sends a newsletter subscription deactivation message
    /// </summary>
    /// <param name="subscription">Newsletter subscription</param>
    /// <param name="languageId">Language identifier</param>
    public virtual async Task<int> SendNewsLetterSubscriptionDeactivationMessage(NewsLetterSubscription subscription,
        string languageId)
    {
        return await SendNewsLetterSubscriptionMessage(
            MessageTemplateNames.SendNewsLetterSubscriptionDeactivationMessage, subscription, languageId);
    }

    /// <summary>
    ///     Send a newsletter subscription message
    /// </summary>
    /// <param name="message">Message template</param>
    /// <param name="subscription">Newsletter subscription</param>
    /// <param name="languageId">Language identifier</param>
    private async Task<int> SendNewsLetterSubscriptionMessage(string message, NewsLetterSubscription subscription,
        string languageId)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        var store = await GetStore(subscription.StoreId);
        var language = await EnsureLanguageIsActive(languageId, store.Id);

        var messageTemplate = await GetMessageTemplate(message, store.Id);
        if (messageTemplate == null)
            return 0;

        //email account
        var emailAccount = await GetEmailAccountOfMessageTemplate(messageTemplate, language.Id);

        var builder = new LiquidObjectBuilder(_mediator);
        builder.AddStoreTokens(store, language, emailAccount)
            .AddNewsLetterSubscriptionTokens(subscription, store, CurrentHost);

        var liquidObject = await builder.BuildAsync();
        //event notification
        await _mediator.MessageTokensAdded(messageTemplate, liquidObject);

        var toEmail = subscription.Email;
        return await SendNotification(messageTemplate, emailAccount,
            languageId, liquidObject,
            toEmail, string.Empty,
            reference: Reference.Customer, objectId: subscription.CustomerId);
    }

    #endregion

    #region Send a message to a friend, ask question

    /// <summary>
    ///     Sends "email a friend" message
    /// </summary>
    /// <param name="customer">Customer instance</param>
    /// <param name="store">Store</param>
    /// <param name="languageId">Message language identifier</param>
    /// <param name="product">Product instance</param>
    /// <param name="customerEmail">Customer's email</param>
    /// <param name="friendsEmail">Friend's email</param>
    /// <param name="personalMessage">Personal message</param>
    /// <returns>Queued email identifier</returns>
    public virtual async Task<int> SendProductEmailAFriendMessage(Customer customer, Store store, string languageId,
        Product product, string customerEmail, string friendsEmail, string personalMessage)
    {
        ArgumentNullException.ThrowIfNull(customer);
        ArgumentNullException.ThrowIfNull(product);

        var language = await EnsureLanguageIsActive(languageId, store.Id);

        var messageTemplate = await GetMessageTemplate(MessageTemplateNames.SendProductEmailAFriendMessage, store.Id);
        if (messageTemplate == null)
            return 0;

        //email account
        var emailAccount = await GetEmailAccountOfMessageTemplate(messageTemplate, language.Id);

        var builder = new LiquidObjectBuilder(_mediator);
        builder.AddStoreTokens(store, language, emailAccount)
            .AddEmailAFriendTokens(personalMessage, customerEmail, friendsEmail)
            .AddCustomerTokens(customer, store, CurrentHost, language)
            .AddProductTokens(product, language, store, CurrentHost);
        var liquidObject = await builder.BuildAsync();

        //event notification
        await _mediator.MessageTokensAdded(messageTemplate, liquidObject);

        var toEmail = friendsEmail;
        var toName = "";
        return await SendNotification(messageTemplate, emailAccount,
            languageId, liquidObject,
            toEmail, toName,
            reference: Reference.Customer, objectId: customer.Id);
    }

    /// <summary>
    ///     Sends wishlist "email a friend" message
    /// </summary>
    /// <param name="customer">Customer</param>
    /// <param name="store">Store</param>
    /// <param name="languageId">Message language identifier</param>
    /// <param name="customerEmail">Customer's email</param>
    /// <param name="friendsEmail">Friend's email</param>
    /// <param name="personalMessage">Personal message</param>
    /// <returns>Queued email identifier</returns>
    public virtual async Task<int> SendWishlistEmailAFriendMessage(Customer customer, Store store, string languageId,
        string customerEmail, string friendsEmail, string personalMessage)
    {
        ArgumentNullException.ThrowIfNull(customer);

        var language = await EnsureLanguageIsActive(languageId, store.Id);

        var messageTemplate = await GetMessageTemplate(MessageTemplateNames.SendWishlistEmailAFriendMessage, store.Id);
        if (messageTemplate == null)
            return 0;

        //email account
        var emailAccount = await GetEmailAccountOfMessageTemplate(messageTemplate, language.Id);


        var builder = new LiquidObjectBuilder(_mediator);
        builder.AddStoreTokens(store, language, emailAccount)
            .AddCustomerTokens(customer, store, CurrentHost, language)
            .AddEmailAFriendTokens(personalMessage, customerEmail, friendsEmail);

        var liquidObject = await builder.BuildAsync();

        //event notification
        await _mediator.MessageTokensAdded(messageTemplate, liquidObject);

        var toEmail = friendsEmail;
        var toName = "";
        return await SendNotification(messageTemplate, emailAccount,
            languageId, liquidObject,
            toEmail, toName,
            reference: Reference.Customer, objectId: customer.Id);
    }


    /// <summary>
    ///     Sends "email a friend" message
    /// </summary>
    /// <returns>Queued email identifier</returns>
    public virtual async Task<int> SendProductQuestionMessage(Customer customer, Store store, string languageId,
        Product product, string customerEmail, string fullName, string phone, string message, string ipaddress)
    {
        ArgumentNullException.ThrowIfNull(customer);
        ArgumentNullException.ThrowIfNull(product);

        var language = await EnsureLanguageIsActive(languageId, store.Id);

        var messageTemplate = await GetMessageTemplate(MessageTemplateNames.SendProductQuestionMessage, store.Id);
        if (messageTemplate == null)
            return 0;

        //email account
        var emailAccount = await GetEmailAccountOfMessageTemplate(messageTemplate, language.Id);

        var builder = new LiquidObjectBuilder(_mediator);
        builder.AddStoreTokens(store, language, emailAccount)
            .AddCustomerTokens(customer, store, CurrentHost, language)
            .AddProductTokens(product, language, store, CurrentHost);
        var liquidObject = await builder.BuildAsync();
        liquidObject.AskQuestion = new LiquidAskQuestion(message, customerEmail, fullName, phone);

        //event notification
        await _mediator.MessageTokensAdded(messageTemplate, liquidObject);

        //store in database
        if (_commonSettings.StoreInDatabaseContactUsForm)
        {
            var subject = messageTemplate.GetTranslation(mt => mt.Subject, languageId);
            var body = messageTemplate.GetTranslation(mt => mt.Body, languageId);

            var subjectReplaced = LiquidExtensions.Render(liquidObject, subject);
            var bodyReplaced = LiquidExtensions.Render(liquidObject, body);

            await _mediator.Send(new InsertContactUsCommand {
                CustomerId = customer.Id,
                StoreId = store.Id,
                VendorId = product.VendorId,
                Email = customerEmail,
                Enquiry = bodyReplaced,
                FullName = fullName,
                Subject = subjectReplaced,
                EmailAccountId = emailAccount.Id,
                RemoteIpAddress = ipaddress
            });
        }

        var toEmail = emailAccount.Email;
        var toName = "";

        if (!string.IsNullOrEmpty(product.VendorId))
        {
            var vendor = await _mediator.Send(new GetVendorByIdQuery { Id = product.VendorId });
            if (vendor != null)
            {
                toEmail = vendor.Email;
                toName = vendor.Name;
            }
        }

        return await SendNotification(messageTemplate, emailAccount,
            languageId, liquidObject,
            toEmail, toName, replyToEmailAddress: customerEmail,
            reference: Reference.Customer, objectId: customer.Id);
    }

    #endregion

    #region Merchandise returns

    /// <summary>
    ///     Sends 'New Merchandise Return' message to a store owner
    /// </summary>
    /// <param name="merchandiseReturn">Merchandise return</param>
    /// <param name="order">Order</param>
    /// <param name="languageId">Message language identifier</param>
    public virtual async Task<int> SendNewMerchandiseReturnStoreOwnerMessage(MerchandiseReturn merchandiseReturn,
        Order order, string languageId)
    {
        ArgumentNullException.ThrowIfNull(merchandiseReturn);

        var store = await GetStore(order.StoreId);
        var language = await EnsureLanguageIsActive(languageId, store.Id);

        var messageTemplate =
            await GetMessageTemplate(MessageTemplateNames.SendNewMerchandiseReturnStoreOwnerMessage, store.Id);
        if (messageTemplate == null)
            return 0;

        //email account
        var emailAccount = await GetEmailAccountOfMessageTemplate(messageTemplate, language.Id);

        var customer = await _mediator.Send(new GetCustomerByIdQuery { Id = merchandiseReturn.CustomerId });

        var builder = new LiquidObjectBuilder(_mediator);
        builder.AddStoreTokens(store, language, emailAccount);

        AddCustomerTokensIfNotNull(builder, customer, store, language);

        builder.AddMerchandiseReturnTokens(merchandiseReturn, store, CurrentHost, order, language);

        var liquidObject = await builder.BuildAsync();

        //event notification
        await _mediator.MessageTokensAdded(messageTemplate, liquidObject);

        if (!string.IsNullOrEmpty(merchandiseReturn.VendorId))
        {
            var vendor = await _mediator.Send(new GetVendorByIdQuery { Id = merchandiseReturn.VendorId });
            if (vendor != null)
            {
                var vendorEmail = vendor.Email;
                var vendorName = vendor.Name;
                await SendNotification(messageTemplate, emailAccount,
                    languageId, liquidObject,
                    vendorEmail, vendorName);
            }
        }

        var toEmail = emailAccount.Email;
        var toName = emailAccount.DisplayName;
        return await SendNotification(messageTemplate, emailAccount,
            languageId, liquidObject,
            toEmail, toName,
            reference: Reference.MerchandiseReturn, objectId: merchandiseReturn.Id);
    }

    /// <summary>
    ///     Sends 'Merchandise Return status changed' message to a customer
    /// </summary>
    /// <param name="merchandiseReturn">Merchandise return</param>
    /// <param name="order">Order</param>
    /// <param name="languageId">Message language identifier</param>
    public virtual async Task<int> SendMerchandiseReturnStatusChangedCustomerMessage(
        MerchandiseReturn merchandiseReturn, Order order, string languageId)
    {
        ArgumentNullException.ThrowIfNull(merchandiseReturn);

        var store = await GetStore(order.StoreId);
        var language = await EnsureLanguageIsActive(languageId, store.Id);

        var messageTemplate =
            await GetMessageTemplate(MessageTemplateNames.SendMerchandiseReturnStatusChangedCustomerMessage, store.Id);
        if (messageTemplate == null)
            return 0;

        //email account
        var emailAccount = await GetEmailAccountOfMessageTemplate(messageTemplate, language.Id);

        var customer = await _mediator.Send(new GetCustomerByIdQuery { Id = merchandiseReturn.CustomerId });

        var builder = new LiquidObjectBuilder(_mediator);
        builder.AddStoreTokens(store, language, emailAccount);

        AddCustomerTokensIfNotNull(builder, customer, store, language);

        builder.AddMerchandiseReturnTokens(merchandiseReturn, store, CurrentHost, order, language);
        var liquidObject = await builder.BuildAsync();
        //event notification
        await _mediator.MessageTokensAdded(messageTemplate, liquidObject);

        var toEmail = string.IsNullOrEmpty(customer?.Email) ? order.BillingAddress.Email : customer.Email;
        var toName = string.IsNullOrEmpty(customer?.Email) ? order.BillingAddress.FirstName : customer.GetFullName();
        return await SendNotification(messageTemplate, emailAccount,
            languageId, liquidObject,
            toEmail, toName,
            reference: Reference.MerchandiseReturn, objectId: merchandiseReturn.Id);
    }

    /// <summary>
    ///     Sends 'New Merchandise Return' message to a customer
    /// </summary>
    /// <param name="merchandiseReturn">Merchandise return</param>
    /// <param name="order">Order</param>
    /// <param name="languageId">Message language identifier</param>
    public virtual async Task<int> SendNewMerchandiseReturnCustomerMessage(MerchandiseReturn merchandiseReturn,
        Order order, string languageId)
    {
        ArgumentNullException.ThrowIfNull(merchandiseReturn);

        var store = await GetStore(order.StoreId);
        var language = await EnsureLanguageIsActive(languageId, store.Id);

        var messageTemplate =
            await GetMessageTemplate(MessageTemplateNames.SendNewMerchandiseReturnCustomerMessage, store.Id);
        if (messageTemplate == null)
            return 0;

        //email account
        var emailAccount = await GetEmailAccountOfMessageTemplate(messageTemplate, language.Id);

        var customer = await _mediator.Send(new GetCustomerByIdQuery { Id = merchandiseReturn.CustomerId });

        var builder = new LiquidObjectBuilder(_mediator);
        builder.AddStoreTokens(store, language, emailAccount);
        AddCustomerTokensIfNotNull(builder, customer, store, language);

        builder.AddMerchandiseReturnTokens(merchandiseReturn, store, CurrentHost, order, language);
        var liquidObject = await builder.BuildAsync();
        //event notification
        await _mediator.MessageTokensAdded(messageTemplate, liquidObject);

        var toEmail = string.IsNullOrEmpty(customer?.Email) ? order.BillingAddress.Email : customer.Email;
        var toName = string.IsNullOrEmpty(customer?.Email) ? order.BillingAddress.FirstName : customer.GetFullName();
        return await SendNotification(messageTemplate, emailAccount,
            languageId, liquidObject,
            toEmail, toName,
            reference: Reference.MerchandiseReturn, objectId: merchandiseReturn.Id);
    }

    /// <summary>
    ///     Sends a new merchandise return note added notification to a customer
    /// </summary>
    /// <param name="merchandiseReturn">Merchandise return</param>
    /// <param name="merchandiseReturnNote">Merchandise return note</param>
    /// <param name="order">Order</param>
    public virtual async Task<int> SendNewMerchandiseReturnNoteAddedCustomerMessage(MerchandiseReturn merchandiseReturn,
        MerchandiseReturnNote merchandiseReturnNote, Order order)
    {
        ArgumentNullException.ThrowIfNull(merchandiseReturnNote);
        ArgumentNullException.ThrowIfNull(merchandiseReturn);

        var store = await GetStore(order.StoreId);
        var language = await EnsureLanguageIsActive(order.CustomerLanguageId, store.Id);

        var messageTemplate =
            await GetMessageTemplate(MessageTemplateNames.SendNewMerchandiseReturnNoteAddedCustomerMessage, store.Id);
        if (messageTemplate == null)
            return 0;

        //email account
        var emailAccount = await GetEmailAccountOfMessageTemplate(messageTemplate, language.Id);

        var customer = await _mediator.Send(new GetCustomerByIdQuery { Id = merchandiseReturn.CustomerId });

        var builder = new LiquidObjectBuilder(_mediator);
        builder.AddStoreTokens(store, language, emailAccount);
        AddCustomerTokensIfNotNull(builder, customer, store, language);

        builder.AddMerchandiseReturnTokens(merchandiseReturn, store, CurrentHost, order, language,
            merchandiseReturnNote);
        var liquidObject = await builder.BuildAsync();
        //event notification
        await _mediator.MessageTokensAdded(messageTemplate, liquidObject);

        var toEmail = string.IsNullOrEmpty(customer?.Email) ? order.BillingAddress.Email : customer.Email;
        var toName = string.IsNullOrEmpty(customer?.Email) ? order.BillingAddress.FirstName : customer.GetFullName();
        return await SendNotification(messageTemplate, emailAccount,
            language.Id, liquidObject,
            toEmail, toName,
            reference: Reference.MerchandiseReturn, objectId: merchandiseReturn.Id);
    }

    #endregion

    #region Misc

    /// <summary>
    ///     Sends 'New vendor account submitted' message to a store owner
    /// </summary>
    /// <param name="customer">Customer</param>
    /// <param name="vendor">Vendor</param>
    /// <param name="store">Store</param>
    /// <param name="languageId">Message language identifier</param>
    public virtual async Task<int> SendNewVendorAccountApplyStoreOwnerMessage(Customer customer, Vendor vendor,
        Store store, string languageId)
    {
        ArgumentNullException.ThrowIfNull(customer);
        ArgumentNullException.ThrowIfNull(vendor);

        var language = await EnsureLanguageIsActive(languageId, store.Id);

        var messageTemplate = await GetMessageTemplate("VendorAccountApply.StoreOwnerNotification", store.Id);
        if (messageTemplate == null)
            return 0;

        //email account
        var emailAccount = await GetEmailAccountOfMessageTemplate(messageTemplate, language.Id);

        var builder = new LiquidObjectBuilder(_mediator).AddStoreTokens(store, language, emailAccount)
            .AddCustomerTokens(customer, store, CurrentHost, language)
            .AddVendorTokens(vendor, language);

        var liquidObject = await builder.BuildAsync();
        //event notification
        await _mediator.MessageTokensAdded(messageTemplate, liquidObject);

        var toEmail = emailAccount.Email;
        var toName = emailAccount.DisplayName;
        return await SendNotification(messageTemplate, emailAccount,
            languageId, liquidObject,
            toEmail, toName,
            reference: Reference.Customer, objectId: customer.Id);
    }

    /// <summary>
    ///     Sends 'Vendor information changed' message to a store owner
    /// </summary>
    /// <param name="vendor">Vendor</param>
    /// <param name="store">Store</param>
    /// <param name="languageId">Message language identifier</param>
    public virtual async Task<int> SendVendorInformationChangeMessage(Vendor vendor, Store store, string languageId)
    {
        ArgumentNullException.ThrowIfNull(vendor);

        var language = await EnsureLanguageIsActive(languageId, store.Id);

        var messageTemplate = await GetMessageTemplate("VendorInformationChange.StoreOwnerNotification", store.Id);
        if (messageTemplate == null)
            return 0;

        //email account
        var emailAccount = await GetEmailAccountOfMessageTemplate(messageTemplate, language.Id);

        var builder = new LiquidObjectBuilder(_mediator);
        builder.AddStoreTokens(store, language, emailAccount)
            .AddVendorTokens(vendor, language);
        var liquidObject = await builder.BuildAsync();
        //event notification
        await _mediator.MessageTokensAdded(messageTemplate, liquidObject);

        var toEmail = emailAccount.Email;
        var toName = emailAccount.DisplayName;

        return await SendNotification(messageTemplate, emailAccount, languageId, liquidObject, toEmail, toName,
            reference: Reference.Vendor, objectId: vendor.Id);
    }


    /// <summary>
    ///     Sends a gift voucher notification
    /// </summary>
    /// <param name="giftVoucher">Gift voucher</param>
    /// <param name="order">Order</param>
    /// <param name="languageId">Message language identifier</param>
    public virtual async Task<int> SendGiftVoucherMessage(GiftVoucher giftVoucher, Order order, string languageId)
    {
        ArgumentNullException.ThrowIfNull(giftVoucher);

        Store store = null;
        if (order != null) store = await _storeService.GetStoreById(order.StoreId);
        store ??= (await _storeService.GetAllStores()).FirstOrDefault();

        var language = await EnsureLanguageIsActive(languageId, store?.Id);

        var messageTemplate = await GetMessageTemplate("GiftVoucher.Notification", store?.Id);
        if (messageTemplate == null)
            return 0;

        //email account
        var emailAccount = await GetEmailAccountOfMessageTemplate(messageTemplate, language.Id);

        var builder = new LiquidObjectBuilder(_mediator);
        builder.AddStoreTokens(store, language, emailAccount)
            .AddGiftVoucherTokens(giftVoucher, language);
        var liquidObject = await builder.BuildAsync();
        //event notification
        await _mediator.MessageTokensAdded(messageTemplate, liquidObject);
        var toEmail = giftVoucher.RecipientEmail;
        var toName = giftVoucher.RecipientName;
        return await SendNotification(messageTemplate, emailAccount,
            languageId, liquidObject,
            toEmail, toName);
    }

    /// <summary>
    ///     Sends a product review notification message to a store owner
    /// </summary>
    /// <param name="product">Product</param>
    /// <param name="productReview">Product review</param>
    /// <param name="store">Store</param>
    /// <param name="languageId">Message language identifier</param>
    public virtual async Task<int> SendProductReviewMessage(Product product, ProductReview productReview,
        Store store, string languageId)
    {
        ArgumentNullException.ThrowIfNull(productReview);

        var language = await EnsureLanguageIsActive(languageId, store.Id);

        var messageTemplate = await GetMessageTemplate("Product.ProductReview", store.Id);
        if (messageTemplate == null)
            return 0;

        //email account
        var emailAccount = await GetEmailAccountOfMessageTemplate(messageTemplate, language.Id);

        var customer = await _mediator.Send(new GetCustomerByIdQuery { Id = productReview.CustomerId });

        var builder = new LiquidObjectBuilder(_mediator);
        builder.AddStoreTokens(store, language, emailAccount)
            .AddProductReviewTokens(product, productReview);
        AddCustomerTokensIfNotNull(builder, customer, store, language);

        var liquidObject = await builder.BuildAsync();
        //event notification
        await _mediator.MessageTokensAdded(messageTemplate, liquidObject);

        var toEmail = emailAccount.Email;
        var toName = emailAccount.DisplayName;
        return await SendNotification(messageTemplate, emailAccount,
            languageId, liquidObject,
            toEmail, toName,
            reference: Reference.Product, objectId: product.Id);
    }


    /// <summary>
    ///     Sends a vendor review notification message to a store owner
    /// </summary>
    /// <param name="vendorReview">Vendor review</param>
    /// <param name="store">Store</param>
    /// <param name="languageId">Message language identifier</param>
    public virtual async Task<int> SendVendorReviewMessage(VendorReview vendorReview, Store store,
        string languageId)
    {
        ArgumentNullException.ThrowIfNull(vendorReview);

        var language = await EnsureLanguageIsActive(languageId, store.Id);

        var messageTemplate = await GetMessageTemplate("Vendor.VendorReview", store.Id);
        if (messageTemplate == null)
            return 0;

        //email account
        var emailAccount = await GetEmailAccountOfMessageTemplate(messageTemplate, language.Id);
        //customer
        var customer = await _mediator.Send(new GetCustomerByIdQuery { Id = vendorReview.CustomerId });
        //vendor
        var vendor = await _mediator.Send(new GetVendorByIdQuery { Id = vendorReview.VendorId });

        var builder = new LiquidObjectBuilder(_mediator);
        builder.AddStoreTokens(store, language, emailAccount)
            .AddVendorReviewTokens(vendor, vendorReview);
        AddCustomerTokensIfNotNull(builder, customer, store, language);

        builder.AddVendorTokens(vendor, language);
        var liquidObject = await builder.BuildAsync();

        //event notification
        await _mediator.MessageTokensAdded(messageTemplate, liquidObject);

        var toEmail = vendor.Email;
        var toName = vendor.Name;
        return await SendNotification(messageTemplate, emailAccount,
            languageId, liquidObject,
            toEmail, toName,
            reference: Reference.Customer, objectId: customer?.Id);
    }

    /// <summary>
    ///     Sends a "quantity below" notification to a store owner
    /// </summary>
    /// <param name="product">Product</param>
    /// <param name="languageId">Message language identifier</param>
    public virtual async Task<int> SendQuantityBelowStoreOwnerMessage(Product product, string languageId)
    {
        ArgumentNullException.ThrowIfNull(product);

        var store = await GetStore("");
        var language = await EnsureLanguageIsActive(languageId, store.Id);

        var messageTemplate = await GetMessageTemplate("QuantityBelow.StoreOwnerNotification", store.Id);
        if (messageTemplate == null)
            return 0;

        //email account
        var emailAccount = await GetEmailAccountOfMessageTemplate(messageTemplate, language.Id);

        var builder = new LiquidObjectBuilder(_mediator);
        builder.AddStoreTokens(store, language, emailAccount)
            .AddProductTokens(product, language, store, CurrentHost);
        var liquidObject = await builder.BuildAsync();
        //event notification
        await _mediator.MessageTokensAdded(messageTemplate, liquidObject);

        var toEmail = emailAccount.Email;
        var toName = emailAccount.DisplayName;
        return await SendNotification(messageTemplate, emailAccount,
            languageId, liquidObject,
            toEmail, toName,
            reference: Reference.Product, objectId: product.Id);
    }

    /// <summary>
    ///     Sends a "quantity below" notification to a store owner
    /// </summary>
    /// <param name="product"></param>
    /// <param name="combination">Attribute combination</param>
    /// <param name="languageId">Message language identifier</param>
    public virtual async Task<int> SendQuantityBelowStoreOwnerMessage(Product product,
        ProductAttributeCombination combination, string languageId)
    {
        ArgumentNullException.ThrowIfNull(combination);

        var store = await GetStore("");
        var language = await EnsureLanguageIsActive(languageId, store.Id);

        var messageTemplate =
            await GetMessageTemplate("QuantityBelow.AttributeCombination.StoreOwnerNotification", store.Id);
        if (messageTemplate == null)
            return 0;

        //email account
        var emailAccount = await GetEmailAccountOfMessageTemplate(messageTemplate, language.Id);

        var builder = new LiquidObjectBuilder(_mediator);
        builder.AddStoreTokens(store, language, emailAccount)
            .AddProductTokens(product, language, store, CurrentHost)
            .AddAttributeCombinationTokens(product, combination);
        var liquidObject = await builder.BuildAsync();
        //event notification
        await _mediator.MessageTokensAdded(messageTemplate, liquidObject);

        var toEmail = emailAccount.Email;
        var toName = emailAccount.DisplayName;
        return await SendNotification(messageTemplate, emailAccount,
            languageId, liquidObject,
            toEmail, toName,
            reference: Reference.Product, objectId: product.Id);
    }

    /// <summary>
    ///     Sends a "new VAT" notification to a store owner
    /// </summary>
    /// <param name="customer">Customer</param>
    /// <param name="languageId">Message language identifier</param>
    public virtual async Task<int> SendCustomerDeleteStoreOwnerMessage(Customer customer, string languageId)
    {
        ArgumentNullException.ThrowIfNull(customer);

        var store = await GetStore("");
        var language = await EnsureLanguageIsActive(languageId, store.Id);

        var messageTemplate = await GetMessageTemplate("CustomerDelete.StoreOwnerNotification", store.Id);
        if (messageTemplate == null)
            return 0;

        //email account
        var emailAccount = await GetEmailAccountOfMessageTemplate(messageTemplate, language.Id);

        var builder = new LiquidObjectBuilder(_mediator);
        builder.AddStoreTokens(store, language, emailAccount)
            .AddCustomerTokens(customer, store, CurrentHost, language);

        var liquidObject = await builder.BuildAsync();

        var toEmail = emailAccount.Email;
        var toName = emailAccount.DisplayName;
        return await SendNotification(messageTemplate, emailAccount,
            languageId, liquidObject,
            toEmail, toName,
            reference: Reference.Customer, objectId: customer.Id);
    }

    /// <summary>
    ///     Sends a blog comment notification message to a store owner
    /// </summary>
    public virtual async Task<int> SendBlogCommentMessage(BlogPost blogPost, BlogComment blogComment, string languageId)
    {
        ArgumentNullException.ThrowIfNull(blogComment);

        var store = await GetStore(blogComment.StoreId);
        var language = await EnsureLanguageIsActive(languageId, store.Id);

        var messageTemplate = await GetMessageTemplate("Blog.BlogComment", store.Id);
        if (messageTemplate == null)
            return 0;

        //email account
        var emailAccount = await GetEmailAccountOfMessageTemplate(messageTemplate, language.Id);

        var builder = new LiquidObjectBuilder(_mediator);
        builder.AddStoreTokens(store, language, emailAccount)
            .AddBlogCommentTokens(blogPost, blogComment, store, CurrentHost, language);

        var customer = await _mediator.Send(new GetCustomerByIdQuery { Id = blogComment.CustomerId });
        if (customer != null && await _groupService.IsRegistered(customer))
            builder.AddCustomerTokens(customer, store, CurrentHost, language);

        var liquidObject = await builder.BuildAsync();
        //event notification
        await _mediator.MessageTokensAdded(messageTemplate, liquidObject);

        var toEmail = emailAccount.Email;
        var toName = emailAccount.DisplayName;
        return await SendNotification(messageTemplate, emailAccount,
            languageId, liquidObject,
            toEmail, toName,
            reference: Reference.Blog, objectId: blogPost.Id);
    }

    /// <summary>
    ///     Sends an article comment notification message to a store owner
    /// </summary>
    /// <param name="article"></param>
    /// <param name="articleComment">Article comment</param>
    /// <param name="languageId">Message language identifier</param>
    public virtual async Task<int> SendArticleCommentMessage(KnowledgebaseArticle article,
        KnowledgebaseArticleComment articleComment, string languageId)
    {
        ArgumentNullException.ThrowIfNull(articleComment);

        var store = await GetStore("");
        var language = await EnsureLanguageIsActive(languageId, store.Id);

        var messageTemplate = await GetMessageTemplate("Knowledgebase.ArticleComment", store.Id);
        if (messageTemplate == null)
            return 0;

        //email account
        var emailAccount = await GetEmailAccountOfMessageTemplate(messageTemplate, language.Id);

        var builder = new LiquidObjectBuilder(_mediator);
        builder.AddStoreTokens(store, language, emailAccount)
            .AddArticleCommentTokens(article, articleComment, store, CurrentHost, language);

        var customer = await _mediator.Send(new GetCustomerByIdQuery { Id = articleComment.CustomerId });
        if (customer != null && await _groupService.IsRegistered(customer))
            builder.AddCustomerTokens(customer, store, CurrentHost, language);
        var liquidObject = await builder.BuildAsync();
        //event notification
        await _mediator.MessageTokensAdded(messageTemplate, liquidObject);

        var toEmail = emailAccount.Email;
        var toName = emailAccount.DisplayName;
        return await SendNotification(messageTemplate, emailAccount,
            languageId, liquidObject,
            toEmail, toName);
    }

    /// <summary>
    ///     Sends a news comment notification message to a store owner
    /// </summary>
    /// <param name="newsItem">News item</param>
    /// <param name="newsComment">News comment</param>
    /// <param name="languageId">Message language identifier</param>
    public virtual async Task<int> SendNewsCommentMessage(NewsItem newsItem, NewsComment newsComment, string languageId)
    {
        ArgumentNullException.ThrowIfNull(newsComment);

        var store = await GetStore(newsComment.StoreId);
        var language = await EnsureLanguageIsActive(languageId, store.Id);

        var messageTemplate = await GetMessageTemplate("News.NewsComment", store.Id);
        if (messageTemplate == null)
            return 0;

        //email account
        var emailAccount = await GetEmailAccountOfMessageTemplate(messageTemplate, language.Id);

        var builder = new LiquidObjectBuilder(_mediator);
        builder.AddStoreTokens(store, language, emailAccount)
            .AddNewsCommentTokens(newsItem, newsComment, store, CurrentHost, language);
        var customer = await _mediator.Send(new GetCustomerByIdQuery { Id = newsComment.CustomerId });
        AddCustomerTokensIfNotNull(builder, customer, store, language);

        var liquidObject = await builder.BuildAsync();
        //event notification
        await _mediator.MessageTokensAdded(messageTemplate, liquidObject);

        var toEmail = emailAccount.Email;
        var toName = emailAccount.DisplayName;
        return await SendNotification(messageTemplate, emailAccount,
            languageId, liquidObject,
            toEmail, toName);
    }

    /// <summary>
    ///     Sends a 'Out of stock' notification message to a customer
    /// </summary>
    /// <param name="customer">Customer</param>
    /// <param name="product">Product</param>
    /// <param name="subscription">Subscription</param>
    /// <param name="languageId">Message language identifier</param>
    public virtual async Task<int> SendBackInStockMessage(Customer customer, Product product,
        OutOfStockSubscription subscription, string languageId)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        var store = await GetStore(subscription.StoreId);
        var language = await EnsureLanguageIsActive(languageId, store.Id);

        var messageTemplate = await GetMessageTemplate("Customer.OutOfStock", store.Id);
        if (messageTemplate == null)
            return 0;

        //email account
        var emailAccount = await GetEmailAccountOfMessageTemplate(messageTemplate, language.Id);

        var builder = new LiquidObjectBuilder(_mediator);
        builder.AddStoreTokens(store, language, emailAccount);
        AddCustomerTokensIfNotNull(builder, customer, store, language);

        builder.AddOutOfStockTokens(product, subscription, store, CurrentHost, language);
        var liquidObject = await builder.BuildAsync();
        //event notification
        await _mediator.MessageTokensAdded(messageTemplate, liquidObject);

        var toEmail = customer?.Email;
        var toName = customer.GetFullName();
        return await SendNotification(messageTemplate, emailAccount,
            languageId, liquidObject,
            toEmail, toName,
            reference: Reference.Customer, objectId: customer?.Id);
    }

    /// <summary>
    ///     Sends "contact us" message
    /// </summary>
    /// <param name="customer">Customer</param>
    /// <param name="store">Store</param>
    /// <param name="languageId">Message language identifier</param>
    /// <param name="senderEmail">Sender email</param>
    /// <param name="senderName">Sender name</param>
    /// <param name="subject">Email subject. Pass null if you want a message template subject to be used.</param>
    /// <param name="body">Email body</param>
    /// <param name="attrInfo">Attr info</param>
    /// <param name="customAttributes">CustomAttributes</param>
    /// <param name="ipaddress">Ip address</param>
    public virtual async Task<int> SendContactUsMessage(Customer customer, Store store, string languageId,
        string senderEmail,
        string senderName, string subject, string body, string attrInfo, IList<CustomAttribute> customAttributes,
        string ipaddress)
    {
        var language = await EnsureLanguageIsActive(languageId, store.Id);
        var messageTemplate = await GetMessageTemplate("Service.ContactUs", store.Id);
        if (messageTemplate == null)
            return 0;

        //email account
        var emailAccount = await GetEmailAccountOfMessageTemplate(messageTemplate, language.Id);

        string fromEmail;
        string fromName;
        senderName = WebUtility.HtmlEncode(senderName);
        senderEmail = WebUtility.HtmlEncode(senderEmail);
        //required for some SMTP servers
        if (_commonSettings.UseSystemEmailForContactUsForm)
        {
            fromEmail = emailAccount.Email;
            fromName = emailAccount.DisplayName;
        }
        else
        {
            fromEmail = senderEmail;
            fromName = senderName;
        }

        var builder = new LiquidObjectBuilder(_mediator);
        builder.AddStoreTokens(store, language, emailAccount)
            .AddCustomerTokens(customer, store, CurrentHost, language);

        var liquidObject = await builder.BuildAsync();
        liquidObject.ContactUs = new LiquidContactUs(senderEmail, senderName, body, attrInfo);
        //event notification
        await _mediator.MessageTokensAdded(messageTemplate, liquidObject);

        var toEmail = emailAccount.Email;
        var toName = emailAccount.DisplayName;

        //store in database
        if (_commonSettings.StoreInDatabaseContactUsForm)
            await _mediator.Send(new InsertContactUsCommand {
                CustomerId = customer.Id,
                StoreId = store.Id,
                VendorId = "",
                Email = senderEmail,
                Enquiry = body,
                FullName = senderName,
                Subject = string.IsNullOrEmpty(subject) ? "Contact Us (form)" : subject,
                ContactAttributeDescription = attrInfo,
                ContactAttributes = customAttributes,
                EmailAccountId = emailAccount.Id,
                RemoteIpAddress = ipaddress
            });
        return await SendNotification(messageTemplate, emailAccount, languageId, liquidObject, toEmail, toName,
            fromEmail: fromEmail,
            fromName: fromName,
            subject: subject,
            replyToEmailAddress: senderEmail,
            replyToName: senderName, reference: Reference.Customer, objectId: customer?.Id);
    }

    /// <summary>
    ///     Sends "contact vendor" message
    /// </summary>
    /// <param name="customer">Customer</param>
    /// <param name="store">Store</param>
    /// <param name="vendor">Vendor</param>
    /// <param name="languageId">Message language identifier</param>
    /// <param name="senderEmail">Sender email</param>
    /// <param name="senderName">Sender name</param>
    /// <param name="subject">Email subject. Pass null if you want a message template subject to be used.</param>
    /// <param name="body">Email body</param>
    /// <param name="ipaddress">Ip address</param>
    public virtual async Task<int> SendContactVendorMessage(Customer customer, Store store, Vendor vendor,
        string languageId, string senderEmail,
        string senderName, string subject, string body, string ipaddress)
    {
        ArgumentNullException.ThrowIfNull(vendor);

        var language = await EnsureLanguageIsActive(languageId, store.Id);

        var messageTemplate = await GetMessageTemplate("Service.ContactVendor", store.Id);
        if (messageTemplate == null)
            return 0;

        //email account
        var emailAccount = await GetEmailAccountOfMessageTemplate(messageTemplate, language.Id);

        string fromEmail;
        string fromName;
        senderName = WebUtility.HtmlEncode(senderName);
        senderEmail = WebUtility.HtmlEncode(senderEmail);

        //required for some SMTP servers
        if (_commonSettings.UseSystemEmailForContactUsForm)
        {
            fromEmail = emailAccount.Email;
            fromName = emailAccount.DisplayName;
        }
        else
        {
            fromEmail = senderEmail;
            fromName = senderName;
        }

        var builder = new LiquidObjectBuilder(_mediator);
        builder.AddStoreTokens(store, language, emailAccount)
            .AddCustomerTokens(customer, store, CurrentHost, language);
        var liquidObject = await builder.BuildAsync();
        liquidObject.ContactUs = new LiquidContactUs(senderEmail, senderName, body, "");
        //event notification
        await _mediator.MessageTokensAdded(messageTemplate, liquidObject);

        var toEmail = vendor.Email;
        var toName = vendor.Name;

        //store in database
        if (_commonSettings.StoreInDatabaseContactUsForm)
            await _mediator.Send(new InsertContactUsCommand {
                CustomerId = customer.Id,
                StoreId = store.Id,
                VendorId = vendor.Id,
                Email = senderEmail,
                Enquiry = body,
                RemoteIpAddress = ipaddress,
                FullName = senderName,
                Subject = string.IsNullOrEmpty(subject) ? "Contact Us (form)" : subject,
                EmailAccountId = emailAccount.Id
            });

        return await SendNotification(messageTemplate, emailAccount, languageId, liquidObject, toEmail, toName,
            fromEmail: fromEmail,
            fromName: fromName,
            subject: subject,
            replyToEmailAddress: senderEmail,
            replyToName: senderName,
            reference: Reference.Vendor, objectId: vendor.Id);
    }

    #region Auction notification

    public virtual async Task<int> SendAuctionWinEndedCustomerMessage(Product product, string languageId, Bid bid)
    {
        ArgumentNullException.ThrowIfNull(product);

        var customer = await _mediator.Send(new GetCustomerByIdQuery { Id = bid.CustomerId });
        if (customer == null) return 0;
        if (string.IsNullOrEmpty(languageId))
            languageId = customer.GetUserFieldFromEntity<string>(SystemCustomerFieldNames.LanguageId);

        var store = await GetStore(bid.StoreId);
        var language = await EnsureLanguageIsActive(languageId, store.Id);

        var messageTemplate = await GetMessageTemplate("AuctionEnded.CustomerNotificationWin", store.Id);
        if (messageTemplate == null)
            return 0;

        var emailAccount = await GetEmailAccountOfMessageTemplate(messageTemplate, language.Id);

        var builder = new LiquidObjectBuilder(_mediator);
        builder.AddAuctionTokens(product, bid)
            .AddCustomerTokens(customer, store, CurrentHost, language)
            .AddProductTokens(product, language, store, CurrentHost)
            .AddStoreTokens(store, language, emailAccount);

        var liquidObject = await builder.BuildAsync();
        //event notification
        await _mediator.MessageTokensAdded(messageTemplate, liquidObject);

        var toEmail = customer.Email;
        var toName = customer.GetFullName();
        return await SendNotification(messageTemplate, emailAccount,
            languageId, liquidObject,
            toEmail, toName,
            reference: Reference.Customer, objectId: customer.Id);
    }

    public virtual async Task<int> SendAuctionEndedLostCustomerMessage(Product product, string languageId, Bid bid)
    {
        ArgumentNullException.ThrowIfNull(product);

        var winner = await _mediator.Send(new GetCustomerByIdQuery { Id = bid.CustomerId });
        if (winner == null) return 0;

        var store = await GetStore(bid.StoreId);

        var language = await EnsureLanguageIsActive(languageId, store.Id);

        var messageTemplate = await GetMessageTemplate("AuctionEnded.CustomerNotificationLost", store.Id);
        if (messageTemplate == null)
            return 0;

        var emailAccount = await GetEmailAccountOfMessageTemplate(messageTemplate, language.Id);

        var builder = new LiquidObjectBuilder(_mediator);
        builder.AddAuctionTokens(product, bid)
            .AddProductTokens(product, language, store, CurrentHost)
            .AddStoreTokens(store, language, emailAccount);
        var liquidObject = await builder.BuildAsync();

        var bids = (await _mediator.Send(new GetBidsByProductIdQuery { ProductId = bid.ProductId }))
            .Where(x => x.CustomerId != bid.CustomerId).GroupBy(x => x.CustomerId);
        foreach (var item in bids)
        {
            var builder2 = new LiquidObjectBuilder(_mediator, liquidObject);
            var customer = await _mediator.Send(new GetCustomerByIdQuery { Id = item.Key });
            if (string.IsNullOrEmpty(languageId))
                languageId = customer.GetUserFieldFromEntity<string>(SystemCustomerFieldNames.LanguageId);
            builder2.AddCustomerTokens(customer, store, CurrentHost, language);
            var liquidObject2 = await builder2.BuildAsync();

            //event notification
            await _mediator.MessageTokensAdded(messageTemplate, liquidObject2);

            var toEmail = customer.Email;
            var toName = customer.GetFullName();
            await SendNotification(messageTemplate, emailAccount,
                languageId, liquidObject2,
                toEmail, toName,
                reference: Reference.Customer, objectId: customer.Id);
        }

        return 0;
    }

    public virtual async Task<int> SendAuctionEndedBinCustomerMessage(Product product, string customerId,
        string languageId, string storeId)
    {
        ArgumentNullException.ThrowIfNull(product);

        var store = await GetStore(storeId);

        var messageTemplate = await GetMessageTemplate("AuctionEnded.CustomerNotificationBin", storeId);
        if (messageTemplate == null)
            return 0;

        var language = await EnsureLanguageIsActive(languageId, store.Id);
        var emailAccount = await GetEmailAccountOfMessageTemplate(messageTemplate, language.Id);

        var builder = new LiquidObjectBuilder(_mediator);
        builder.AddProductTokens(product, language, store, CurrentHost)
            .AddStoreTokens(store, language, emailAccount);

        var liquidObject = await builder.BuildAsync();
        var bids = (await _mediator.Send(new GetBidsByProductIdQuery { ProductId = product.Id }))
            .Where(x => x.CustomerId != customerId).GroupBy(x => x.CustomerId);
        foreach (var item in bids)
        {
            var builder2 = new LiquidObjectBuilder(_mediator, liquidObject);
            var customer = await _mediator.Send(new GetCustomerByIdQuery { Id = item.Key });
            if (customer != null)
            {
                if (string.IsNullOrEmpty(languageId))
                    languageId = customer.GetUserFieldFromEntity<string>(SystemCustomerFieldNames.LanguageId);

                AddCustomerTokensIfNotNull(builder2, customer, store, language);
                var liquidObject2 = await builder2.BuildAsync();
                //event notification
                await _mediator.MessageTokensAdded(messageTemplate, liquidObject2);

                var toEmail = customer.Email;
                var toName = customer.GetFullName();
                await SendNotification(messageTemplate, emailAccount,
                    languageId, liquidObject2,
                    toEmail, toName,
                    reference: Reference.Customer, objectId: customer.Id);
            }
        }

        return 0;
    }

    public virtual async Task<int> SendAuctionEndedStoreOwnerMessage(Product product, string languageId, Bid bid)
    {
        ArgumentNullException.ThrowIfNull(product);

        var builder = new LiquidObjectBuilder(_mediator);
        MessageTemplate messageTemplate;
        EmailAccount emailAccount;

        if (bid != null)
        {
            var customer = await _mediator.Send(new GetCustomerByIdQuery { Id = bid.CustomerId });
            if (string.IsNullOrEmpty(languageId))
                languageId = customer.GetUserFieldFromEntity<string>(SystemCustomerFieldNames.LanguageId);

            var store = await GetStore(bid.StoreId);

            var language = await EnsureLanguageIsActive(languageId, store.Id);

            messageTemplate = await GetMessageTemplate("AuctionEnded.StoreOwnerNotification", store.Id);
            if (messageTemplate == null)
                return 0;

            emailAccount = await GetEmailAccountOfMessageTemplate(messageTemplate, language.Id);
            builder.AddAuctionTokens(product, bid)
                .AddCustomerTokens(customer, store, CurrentHost, language)
                .AddStoreTokens(store, language, emailAccount);
        }
        else
        {
            var store = (await _storeService.GetAllStores()).FirstOrDefault();
            var language = await EnsureLanguageIsActive(languageId, store?.Id);
            messageTemplate = await GetMessageTemplate("AuctionExpired.StoreOwnerNotification", "");
            if (messageTemplate == null)
                return 0;

            emailAccount = await GetEmailAccountOfMessageTemplate(messageTemplate, language.Id);
            builder.AddProductTokens(product, language, store, CurrentHost);
        }

        var liquidObject = await builder.BuildAsync();
        //event notification
        await _mediator.MessageTokensAdded(messageTemplate, liquidObject);

        var toEmail = emailAccount.Email;
        var toName = emailAccount.DisplayName;

        return await SendNotification(messageTemplate, emailAccount,
            languageId, liquidObject,
            toEmail, toName);
    }


    /// <summary>
    ///     Send outbid notification to a customer
    /// </summary>
    /// <param name="languageId">Message language identifier</param>
    /// <param name="product">Product</param>
    /// <param name="bid"></param>
    public virtual async Task<int> SendOutBidCustomerMessage(Product product, string languageId, Bid bid)
    {
        ArgumentNullException.ThrowIfNull(product);

        var customer = await _mediator.Send(new GetCustomerByIdQuery { Id = bid.CustomerId });
        if (string.IsNullOrEmpty(languageId))
            languageId = customer.GetUserFieldFromEntity<string>(SystemCustomerFieldNames.LanguageId);

        var store = await GetStore(bid.StoreId);

        var language = await EnsureLanguageIsActive(languageId, store.Id);

        var messageTemplate = await GetMessageTemplate("BidUp.CustomerNotification", store.Id);
        if (messageTemplate == null)
            return 0;

        var emailAccount = await GetEmailAccountOfMessageTemplate(messageTemplate, language.Id);

        var builder = new LiquidObjectBuilder(_mediator);
        builder.AddAuctionTokens(product, bid)
            .AddCustomerTokens(customer, store, CurrentHost, language)
            .AddStoreTokens(store, language, emailAccount);

        var liquidObject = await builder.BuildAsync();
        //event notification
        await _mediator.MessageTokensAdded(messageTemplate, liquidObject);

        var toEmail = customer.Email;
        var toName = customer.GetFullName();
        return await SendNotification(messageTemplate, emailAccount,
            languageId, liquidObject,
            toEmail, toName,
            reference: Reference.Customer, objectId: customer.Id);
    }

    #endregion

    public virtual async Task<int> SendNotification(MessageTemplate messageTemplate,
        EmailAccount emailAccount, string languageId, LiquidObject liquidObject,
        string toEmailAddress, string toName,
        string attachmentFilePath = null, string attachmentFileName = null,
        IEnumerable<string> attachedDownloads = null,
        string replyToEmailAddress = null, string replyToName = null,
        string fromEmail = null, string fromName = null, string subject = null,
        Reference reference = Reference.None, string objectId = "")
    {
        if (string.IsNullOrEmpty(toEmailAddress))
            return 0;

        //retrieve translation message template data
        var bcc = messageTemplate.GetTranslation(mt => mt.BccEmailAddresses, languageId);

        if (string.IsNullOrEmpty(subject))
            subject = messageTemplate.GetTranslation(mt => mt.Subject, languageId);

        var body = messageTemplate.GetTranslation(mt => mt.Body, languageId);

        var email = new QueuedEmail();
        liquidObject.Email = new LiquidEmail(email.Id);

        var subjectReplaced = LiquidExtensions.Render(liquidObject, subject);
        var bodyReplaced = LiquidExtensions.Render(liquidObject, body);

        var attachments = new List<string>();
        if (attachedDownloads != null)
            attachments.AddRange(attachedDownloads);
        if (!string.IsNullOrEmpty(messageTemplate.AttachedDownloadId))
            attachments.Add(messageTemplate.AttachedDownloadId);

        //limit name length
        toName = CommonHelper.EnsureMaximumLength(toName, 300);
        email.PriorityId = QueuedEmailPriority.High;
        email.From = !string.IsNullOrEmpty(fromEmail) ? fromEmail : emailAccount.Email;
        email.FromName = !string.IsNullOrEmpty(fromName) ? fromName : emailAccount.DisplayName;
        email.To = toEmailAddress;
        email.ToName = toName;
        email.ReplyTo = replyToEmailAddress;
        email.ReplyToName = replyToName;
        email.CC = string.Empty;
        email.Bcc = bcc;
        email.Subject = subjectReplaced;
        email.Body = bodyReplaced;
        email.AttachmentFilePath = attachmentFilePath;
        email.AttachmentFileName = attachmentFileName;
        email.AttachedDownloads = attachments;
        email.EmailAccountId = emailAccount.Id;
        email.DontSendBeforeDateUtc = !messageTemplate.DelayBeforeSend.HasValue
            ? null
            : DateTime.UtcNow +
              TimeSpan.FromHours(messageTemplate.DelayPeriodId.ToHours(messageTemplate.DelayBeforeSend.Value));
        email.Reference = reference;
        email.ObjectId = objectId;

        await _queuedEmailService.InsertQueuedEmail(email);
        return 1;
    }

    #endregion

    #endregion
}