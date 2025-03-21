using Grand.Business.Core.Interfaces.Checkout.CheckoutAttributes;
using Grand.Data;
using Grand.Domain.Customers;
using Grand.Domain.Orders;
using Grand.Infrastructure;
using Grand.Infrastructure.Caching;
using Grand.Infrastructure.Caching.Constants;
using Grand.Infrastructure.Configuration;
using Grand.Infrastructure.Extensions;
using MediatR;

namespace Grand.Business.Checkout.Services.CheckoutAttributes;

/// <summary>
///     Checkout attribute service
/// </summary>
public class CheckoutAttributeService : ICheckoutAttributeService
{
    #region Ctor

    /// <summary>
    ///     Ctor
    /// </summary>
    public CheckoutAttributeService(
        ICacheBase cacheBase,
        IRepository<CheckoutAttribute> checkoutAttributeRepository,
        IMediator mediator,
        IContextAccessor contextAccessor, AccessControlConfig accessControlConfig)
    {
        _cacheBase = cacheBase;
        _checkoutAttributeRepository = checkoutAttributeRepository;
        _mediator = mediator;
        _contextAccessor = contextAccessor;
        _accessControlConfig = accessControlConfig;
    }

    #endregion

    #region Fields

    private readonly IRepository<CheckoutAttribute> _checkoutAttributeRepository;
    private readonly IMediator _mediator;
    private readonly ICacheBase _cacheBase;
    private readonly IContextAccessor _contextAccessor;
    private readonly AccessControlConfig _accessControlConfig;

    #endregion

    #region Methods

    /// <summary>
    ///     Gets all checkout attributes
    /// </summary>
    /// <param name="storeId">Store identifier</param>
    /// <param name="excludeShippableAttributes">A value indicating whether we should exclude shippable attributes</param>
    /// <param name="ignoreAcl"></param>
    /// <returns>Checkout attributes</returns>
    public virtual async Task<IList<CheckoutAttribute>> GetAllCheckoutAttributes(string storeId = "",
        bool excludeShippableAttributes = false, bool ignoreAcl = false)
    {
        var key = string.Format(CacheKey.CHECKOUTATTRIBUTES_ALL_KEY, storeId, excludeShippableAttributes, ignoreAcl);
        return await _cacheBase.GetAsync(key, async () =>
        {
            var query = from p in _checkoutAttributeRepository.Table
                select p;

            query = query.OrderBy(c => c.DisplayOrder);

            if ((!string.IsNullOrEmpty(storeId) && !_accessControlConfig.IgnoreStoreLimitations) ||
                (!ignoreAcl && !_accessControlConfig.IgnoreAcl))
            {
                if (!ignoreAcl && !_accessControlConfig.IgnoreAcl)
                {
                    var allowedCustomerGroupsIds = _contextAccessor.WorkContext.CurrentCustomer.GetCustomerGroupIds();
                    query = from p in query
                        where !p.LimitedToGroups || allowedCustomerGroupsIds.Any(x => p.CustomerGroups.Contains(x))
                        select p;
                }

                //Store acl
                if (!string.IsNullOrEmpty(storeId) && !_accessControlConfig.IgnoreStoreLimitations)
                    query = from p in query
                        where !p.LimitedToStores || p.Stores.Contains(storeId)
                        select p;
            }

            if (excludeShippableAttributes) query = query.Where(x => !x.ShippableProductRequired);
            return await Task.FromResult(query.ToList());
        });
    }

    /// <summary>
    ///     Gets a checkout attribute
    /// </summary>
    /// <param name="checkoutAttributeId">Checkout attribute identifier</param>
    /// <returns>Checkout attribute</returns>
    public virtual Task<CheckoutAttribute> GetCheckoutAttributeById(string checkoutAttributeId)
    {
        var key = string.Format(CacheKey.CHECKOUTATTRIBUTES_BY_ID_KEY, checkoutAttributeId);
        return _cacheBase.GetAsync(key, () => _checkoutAttributeRepository.GetByIdAsync(checkoutAttributeId));
    }

    /// <summary>
    ///     Inserts a checkout attribute
    /// </summary>
    /// <param name="checkoutAttribute">Checkout attribute</param>
    public virtual async Task InsertCheckoutAttribute(CheckoutAttribute checkoutAttribute)
    {
        ArgumentNullException.ThrowIfNull(checkoutAttribute);

        await _checkoutAttributeRepository.InsertAsync(checkoutAttribute);

        await _cacheBase.RemoveByPrefix(CacheKey.CHECKOUTATTRIBUTES_PATTERN_KEY);
        await _cacheBase.RemoveByPrefix(CacheKey.CHECKOUTATTRIBUTEVALUES_PATTERN_KEY);

        //event notification
        await _mediator.EntityInserted(checkoutAttribute);
    }

    /// <summary>
    ///     Updates the checkout attribute
    /// </summary>
    /// <param name="checkoutAttribute">Checkout attribute</param>
    public virtual async Task UpdateCheckoutAttribute(CheckoutAttribute checkoutAttribute)
    {
        ArgumentNullException.ThrowIfNull(checkoutAttribute);

        await _checkoutAttributeRepository.UpdateAsync(checkoutAttribute);

        await _cacheBase.RemoveByPrefix(CacheKey.CHECKOUTATTRIBUTES_PATTERN_KEY);
        await _cacheBase.RemoveByPrefix(CacheKey.CHECKOUTATTRIBUTEVALUES_PATTERN_KEY);

        //event notification
        await _mediator.EntityUpdated(checkoutAttribute);
    }

    /// <summary>
    ///     Deletes a checkout attribute
    /// </summary>
    /// <param name="checkoutAttribute">Checkout attribute</param>
    public virtual async Task DeleteCheckoutAttribute(CheckoutAttribute checkoutAttribute)
    {
        ArgumentNullException.ThrowIfNull(checkoutAttribute);

        await _checkoutAttributeRepository.DeleteAsync(checkoutAttribute);

        await _cacheBase.RemoveByPrefix(CacheKey.CHECKOUTATTRIBUTES_PATTERN_KEY);
        await _cacheBase.RemoveByPrefix(CacheKey.CHECKOUTATTRIBUTEVALUES_PATTERN_KEY);

        //event notification
        await _mediator.EntityDeleted(checkoutAttribute);
    }

    #endregion
}