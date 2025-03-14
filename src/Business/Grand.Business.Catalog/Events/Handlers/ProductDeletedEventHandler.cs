﻿using Grand.Data;
using Grand.Domain.Catalog;
using Grand.Domain.Customers;
using Grand.Domain.Seo;
using Grand.Infrastructure.Events;
using MediatR;
using System.Text.Json;

namespace Grand.Business.Catalog.Events.Handlers;

public class ProductDeletedEventHandler : INotificationHandler<EntityDeleted<Product>>
{
    private readonly IRepository<CustomerGroupProduct> _customerGroupProductRepository;
    private readonly IRepository<Customer> _customerRepository;
    private readonly IRepository<EntityUrl> _entityUrlRepository;
    private readonly IRepository<ProductDeleted> _productDeletedRepository;
    private readonly IRepository<Product> _productRepository;
    private readonly IRepository<ProductReview> _productReviewRepository;
    private readonly IRepository<ProductTag> _productTagRepository;

    public ProductDeletedEventHandler(
        IRepository<Product> productRepository,
        IRepository<CustomerGroupProduct> customerGroupProductRepository,
        IRepository<Customer> customerRepository,
        IRepository<EntityUrl> entityUrlRepository,
        IRepository<ProductTag> productTagRepository,
        IRepository<ProductReview> productReviewRepository,
        IRepository<ProductDeleted> productDeletedRepository)
    {
        _productRepository = productRepository;
        _customerGroupProductRepository = customerGroupProductRepository;
        _customerRepository = customerRepository;
        _entityUrlRepository = entityUrlRepository;
        _productTagRepository = productTagRepository;
        _productReviewRepository = productReviewRepository;
        _productDeletedRepository = productDeletedRepository;
    }

    public async Task Handle(EntityDeleted<Product> notification, CancellationToken cancellationToken)
    {
        //delete related product
        await _productRepository.PullFilter(string.Empty, x => x.RelatedProducts, z => z.ProductId2,
            notification.Entity.Id);

        //delete similar product
        await _productRepository.PullFilter(string.Empty, x => x.SimilarProducts, z => z.ProductId2,
            notification.Entity.Id);

        //delete cross sales product
        await _productRepository.Pull(string.Empty, x => x.CrossSellProduct, notification.Entity.Id);

        //delete recommended product
        await _productRepository.Pull(string.Empty, x => x.RecommendedProduct, notification.Entity.Id);

        //delete review
        await _productReviewRepository.DeleteManyAsync(x => x.ProductId == notification.Entity.Id);

        //delete from shopping cart
        await _customerRepository.PullFilter(string.Empty, x => x.ShoppingCartItems, z => z.ProductId,
            notification.Entity.Id);

        //delete customer group product
        await _customerGroupProductRepository.DeleteManyAsync(x => x.ProductId == notification.Entity.Id);

        //delete url
        await _entityUrlRepository.DeleteManyAsync(x =>
            x.EntityId == notification.Entity.Id && x.EntityName == EntityTypes.Product);

        //delete product tags
        var existingProductTags = _productTagRepository.Table
            .Where(x => notification.Entity.ProductTags.ToList().Contains(x.Name)).ToList();

        foreach (var tag in existingProductTags)
            await _productTagRepository.UpdateField(tag.Id, x => x.Count, tag.Count - 1);

        //insert to deleted products
        var productDeleted = JsonSerializer.Deserialize<ProductDeleted>(JsonSerializer.Serialize(notification.Entity));
        if (productDeleted != null)
        {
            productDeleted.DeletedOnUtc = DateTime.UtcNow;
            await _productDeletedRepository.InsertAsync(productDeleted);
        }
    }
}