using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutoMapper;
using FBC.Web.Attributes;
using FBC.Web.DTO;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FBC.Web.Controllers
{
	[ApiController]
	[Route("api/[controller]")]
	public class BannerController : ControllerBase
	{
		private readonly ILogger<BannerController> logger;
		private readonly IMapper mapper;
		private readonly Domain.Context context;

		public BannerController(ILogger<BannerController> logger, IMapper mapper, Domain.Context context)
		{
			this.logger = logger;
			this.mapper = mapper;
			this.context = context;
		}

		[HttpGet]
		public async Task<ActionResult<DTO.Responses.Banner>> GetBannersAsync([FromQuery] bool includeDeleted = false)
		{
			List<Domain.Banner> banners;

			if (includeDeleted)
			{
				banners = await context.Banners.ToListAsync();
			}
			else
			{
				banners = await context.Banners.Where(b => !b.Deleted).ToListAsync();
			}
			
			return Ok(banners.Select(b => mapper.Map<Domain.Banner, DTO.Responses.Banner>(b)).ToList());
		}

		// PagedEntity will include total count along with results
		// if we want to merge with above method, then need to update bot call because different response
		[HttpGet("paged")]
		public async Task<ActionResult<DTO.Responses.PagedEntity<DTO.Responses.Banner>>> GetPagedBannersAsync(
			[FromQuery] bool includeDeleted = false,
			[FromQuery] string appSource = "bot",
			[FromQuery] string orderBy = null,
			[FromQuery] string order = null,
			[FromQuery] int? offset = null,
			[FromQuery] int? perPage = null,
			[FromQuery] bool trash = false,
			[FromQuery] string searchQuery = null,
			[FromQuery] Guid? retailerId = null)
		{
			var queryableBanners = GetQueryableBanners(appSource);

			// trash includes only deleted, includeDeleted includes all
			if (!includeDeleted && trash)
			{
				// only get trash results for past 30 days
				var utcNowDate = DateTime.UtcNow.Date;
				queryableBanners = queryableBanners
					.Where(b => b.Deleted && b.DeleteDate.Value.Date >= utcNowDate.AddDays(-30));
			}
			else if (!includeDeleted && !trash)
			{
				queryableBanners = queryableBanners
					.Where(b => !b.Deleted);
			}

			// apply search query
			if (!string.IsNullOrWhiteSpace(searchQuery))
			{
				queryableBanners = queryableBanners
					.Where(b => b.Name.Contains(searchQuery, StringComparison.OrdinalIgnoreCase));
			}

			// apply filters
			if (retailerId.HasValue)
			{
				queryableBanners = queryableBanners
					.Where(b => b.RetailerId == retailerId.Value);
			}

			// order results
			if (!string.IsNullOrWhiteSpace(orderBy) && !string.IsNullOrWhiteSpace(order))
			{
				if (order.Equals("asc", StringComparison.OrdinalIgnoreCase))
				{
					//queryableBanners = queryableBanners.OrderBy(b => b.Name);
					queryableBanners = orderBy switch
					{
						"retailerName" => queryableBanners.OrderBy(b => b.Retailer.Name),

						_ => queryableBanners.OrderBy(b => b.Name),
					};
				}
				else
				{
					//queryableBanners = queryableBanners.OrderByDescending(b => b.Name);
					queryableBanners = orderBy switch
					{
						"retailerName" => queryableBanners.OrderByDescending(b => b.Retailer.Name),

						_ => queryableBanners.OrderByDescending(b => b.Name),
					};
				}
			}

			var bannersCount = await queryableBanners.CountAsync();

			// paginate final results
			if (offset.HasValue && perPage.HasValue)
			{
				queryableBanners = queryableBanners
					.Skip(offset.Value)
					.Take(perPage.Value);
			}

			var banners = await queryableBanners.ToListAsync();

			var pagedBanners = new DTO.Responses.PagedEntity<DTO.Responses.Banner>()
			{
				TotalCount = bannersCount,
				Entities = banners.Select(b => mapper.Map<Domain.Banner, DTO.Responses.Banner>(b)).ToList(),
			};

			return Ok(pagedBanners);
		}

		[HttpGet("counts")]
		public async Task<ActionResult<DTO.Responses.EntityCounts>> GetBannersCountsAsync()
		{
			var utcNowDate = DateTime.UtcNow.Date;

			var bannersCounts = new DTO.Responses.EntityCounts()
			{
				Total = await context.Banners.CountAsync(b => !b.Deleted),
				// only get trash counts for past 30 days
				Deleted = await context.Banners
					.CountAsync(b => b.Deleted && b.DeleteDate.Value.Date >= utcNowDate.AddDays(-30)),
			};

			return Ok(bannersCounts);
		}

		[HttpGet("{bannerId}")]
		public async Task<ActionResult<DTO.Responses.Banner>> GetBannerAsync(Guid bannerId, [FromQuery] string appSource = "bot")
		{
			var banner = await GetQueryableBanners(appSource)
				.FirstOrDefaultAsync(b => b.Id == bannerId);

			if (banner == null)
			{
				return NotFound();
			}

			return Ok(mapper.Map<Domain.Banner, DTO.Responses.Banner>(banner));
		}

		[HttpGet("{bannerId}/stores")]
		public async Task<ActionResult<IList<DTO.Responses.Store>>> GetBannerStoresAsync(Guid bannerId, [FromQuery] bool includeDeleted = false)
		{
			List<Domain.Store> stores;

			if (includeDeleted) 
			{
				stores = await context.Stores.Where(b => b.BannerId == bannerId).ToListAsync();
			}
			else
			{
				stores = await context.Stores.Where(b => b.BannerId == bannerId && !b.Deleted).ToListAsync();
			}

			return Ok(stores.Select(b => mapper.Map<Domain.Store, DTO.Responses.Store>(b)).ToList());
		}

		[Authorize]
		[ClaimsFilter]
		[HttpPut("{id}")]
		public async Task<ActionResult<DTO.Responses.Banner>> UpdateBannerAsync(
			string userId,
			Guid id,
			[FromBody] DTO.Requests.Banner body)
		{
			var banner = await context.Banners
				.FirstOrDefaultAsync(b => b.Id == id);

			if (banner == null)
			{
				return NotFound();
			}

			banner.UpdateName(body.Name, userId);
			banner.UpdateRetailer(body.RetailerId, userId);

			await context.SaveChangesAsync();

			return Ok(mapper.Map<Domain.Banner, DTO.Responses.Banner>(banner));
		}

		[Authorize]
		[ClaimsFilter]
		[HttpDelete("{id}")]
		public async Task<ActionResult> DeleteBannerAsync(
			string userId,
			Guid id,
			[FromQuery] string appSource = "bot")
		{
			var banner = await context.Banners.FindAsync(id);

			if (banner == null)
			{
				return NotFound();
			}
			
			banner.Delete(userId, appSource);

			await context.SaveChangesAsync();

			return Ok();
		}

		[Authorize]
		[ClaimsFilter]
		[HttpPost("bulk-delete")]
		public async Task<ActionResult> DeleteBannersAsync(
			string userId,
			[FromBody] Guid[] ids,
			[FromQuery] string appSource = "bot")
		{
			foreach (var id in ids)
			{
				var banner = await context.Banners.FindAsync(id);

				if (banner != null)
				{
					banner.Delete(userId, appSource);
				}
			}

			await context.SaveChangesAsync();

			return Ok();
		}

		[Authorize]
		[ClaimsFilter]
		[HttpPut("restore/{id}")]
		public async Task<ActionResult> RestoreBannerAsync(string userId, Guid id)
		{
			var banner = await context.Banners.FindAsync(id);

			if (banner == null)
			{
				return NotFound();
			}

			banner.Restore(userId);

			await context.SaveChangesAsync();

			return Ok(mapper.Map<Domain.Banner, DTO.Responses.Banner>(banner));
		}

		[Authorize]
		[ClaimsFilter]
		[HttpPut("bulk-restore")]
		public async Task<ActionResult> RestoreBannersAsync(string userId, [FromBody] Guid[] ids)
		{
			foreach (var id in ids)
			{
				var banner = await context.Banners.FindAsync(id);

				if (banner != null)
				{
					banner.Restore(userId);
				}
			}

			await context.SaveChangesAsync();

			return Ok();
		}

		// include different entities depending on caller
		private IQueryable<Domain.Banner> GetQueryableBanners(string appSource)
		{
			return appSource switch
			{
				"bot" => context.Banners,

				"plugin" => context.Banners
							.Include(b => b.Retailer),

				_ => context.Banners
			};
		}
	}
}
