using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using AutoMapper;
using FBC.Domain;
using FBC.Web.Attributes;
using FBC.Web.DTO;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Org.BouncyCastle.Asn1;

namespace FBC.Web.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DonationController : ControllerBase
    {
        private readonly ILogger<DonationController> logger;
        private readonly IMapper mapper;
        private readonly Domain.Context context;

        public DonationController(ILogger<DonationController> logger, IMapper mapper, Domain.Context context)
        {
            this.logger = logger;
            this.mapper = mapper;
            this.context = context;
        }

        [HttpGet("{id}")]
        public async Task<ActionResult<DTO.Responses.Donation>> GetDonationAsync(Guid id, [FromQuery] string appSource = "bot")
        {

            var donation = await GetQueryableDonations(appSource)
                .FirstOrDefaultAsync(d => d.Id == id);

            if (donation == null)
            {
                return NotFound();
            }

            var obj = mapper.Map<Domain.Donation, DTO.Responses.Donation>(donation);
            if (donation.CreatedBy != null) {
                var user = await context.Users.FirstOrDefaultAsync(d => d.Id == donation.CreatedBy);
                if (user != null)
                {
                    obj.CreatedBy = user.UserName;
                }
            }

            if (donation.LastModifiedBy != null)
            {
                var user = await context.Users.FirstOrDefaultAsync(d => d.Id == donation.LastModifiedBy);
                if (user != null)
                {
                    obj.LastModifiedByUsername = user.UserName;
                }
            }

            return Ok(obj);

        }

        [HttpGet]
        public async Task<ActionResult<IList<DTO.Responses.Donation>>> GetDonationsAsync([FromQuery] bool includeDeleted = false)
        {
            List<Domain.Donation> donations;

            if (includeDeleted)
            {
                donations = await context.Donations
                    .Include(d => d.Store)
                    .Include(d => d.Categories)
                    .ThenInclude(d => d.Category)
                    .ToListAsync();
            }
            else
            {
                donations = await context.Donations
                    .Where(d => !d.Deleted)
                    .Include(d => d.Store)
                    .Include(d => d.Categories)
                    .ThenInclude(d => d.Category)
                    .ToListAsync();
            }

            return Ok(donations.Select(d => mapper.Map<Domain.Donation, DTO.Responses.Donation>(d)).ToList());

        }

        [HttpGet("pagedaggregatedbybranch")]
        public async Task<ActionResult<DTO.Responses.PagedEntity<DTO.Responses.Donation>>> GetPagedDonationsAggregatedByBranchAsync(
            [FromQuery] bool includeDeleted = false,
            [FromQuery] string appSource = "plugin",
            [FromQuery] string searchQuery = null,
            [FromQuery] string orderBy = null,
            [FromQuery] string order = null,
            [FromQuery] int? offset = null,
            [FromQuery] int? perPage = null,
            [FromQuery] int? year = null,
            [FromQuery] int? month = null,
            [FromQuery] Guid? branchId = null,
            [FromQuery] DateTime? startDate = null,
            [FromQuery] DateTime? endDate = null)
        {
            var queryableDonations = GetQueryableDonations(appSource);

            if (!includeDeleted)
            {
                queryableDonations = queryableDonations
                    .Where(d => !d.Deleted);
            }

            var useFilteredCount = !string.IsNullOrWhiteSpace(searchQuery) || year.HasValue ||
                month.HasValue || branchId.HasValue;

            // apply search query
            if (!string.IsNullOrWhiteSpace(searchQuery))
            {
                queryableDonations = queryableDonations
                    .Where(d => d.Branch.Name.Contains(searchQuery, StringComparison.OrdinalIgnoreCase));
            }

            // apply filters
            if (year.HasValue)
            {
                queryableDonations = queryableDonations
                    .Where(d => d.Date != null && d.Date.Year == year.Value);
            }

            if (month.HasValue)
            {
                queryableDonations = queryableDonations
                    .Where(d => d.Date != null && d.Date.Month == month.Value);
            }

            if (branchId.HasValue)
            {
                queryableDonations = queryableDonations
                    .Where(d => d.Branch.Id == branchId.Value);
            }

            if (startDate != null && endDate != null)
            {
                queryableDonations = queryableDonations.Where(d => d.Date >= (DateTime)startDate && d.Date <= (DateTime)endDate);
            }

            try
            {
                var dqueryableDonations = queryableDonations
                        .GroupBy(c => new
                        {
                            c.BranchId,
                            c.Branch.Name,
                            c.Branch.Province,
                            c.Branch.IsAffiliated,                            
                            c.Branch.IsRFP,
                            c.Date.Month,
                            c.Date.Year,
                            //c.Submitted,
                        })
                       .Select(d => new DTO.Responses.DonationByBranch
                       {
                           Id = d.Key.BranchId,
                           BranchName = d.Key.Name,
                           IsAffiliated = d.Key.IsAffiliated,
                           IsRFP = d.Key.IsRFP,
                           Province = d.Key.Province,
                           Count = d.Count(),
                           Year = d.Key.Year,
                           Month = d.Key.Month,
                           TotalWeightInPounds = d.Sum(w => w.WeightInPounds),
                           Submitted = false,//null,
                       });

                IQueryable<DonationSubmission> dsqueryable = GetQueryableDonationsByBranch(appSource);
                var newqueryableDonations = from d in dqueryableDonations
                                            join ds in dsqueryable on new { Y = d.Year, M = d.Month, Id = d.Id } equals new { Y = ds.Year, M = ds.Month, Id = ds.BranchId }
                            into joinData

                            from ds in joinData.DefaultIfEmpty()
                            select (new DTO.Responses.DonationByBranch
                            {
                                Id = d.Id,
                                BranchName = d.BranchName,
                                IsAffiliated = d.IsAffiliated,
                                IsRFP = d.IsRFP,
                                Province = d.Province,
                                Count = d.Count,
                                Year = d.Year,
                                Month = d.Month,
                                TotalWeightInPounds = d.TotalWeightInPounds,
                                Submitted = ds.Submitted,//null,
                                Comments = ds.Comments
                            });

                if (!string.IsNullOrWhiteSpace(orderBy) && !string.IsNullOrWhiteSpace(order))
                {
                    if (order.Equals("asc", StringComparison.OrdinalIgnoreCase))
                    {
                        newqueryableDonations = orderBy switch
                        {
                            "count" => newqueryableDonations.OrderBy(d => d.Count).ThenByDescending(d => d.Year).ThenByDescending(d => d.Month),

                            "totalWeightInPounds" => newqueryableDonations.OrderBy(d => d.TotalWeightInPounds).ThenByDescending(d => d.Year).ThenByDescending(d => d.Month),

                            "province" => newqueryableDonations.OrderBy(d => d.Province).ThenByDescending(d => d.Year).ThenByDescending(d => d.Month),

                            "branchName" => newqueryableDonations.OrderBy(d => d.BranchName).ThenByDescending(d => d.Year).ThenByDescending(d => d.Month),

                            "month" => newqueryableDonations.OrderBy(d => d.Month).ThenByDescending(d => d.Year),

                            "year" => newqueryableDonations.OrderBy(d => d.Year).ThenByDescending(d => d.Month).ThenBy(d => d.BranchName),

                            _ => newqueryableDonations.OrderByDescending(d=>d.Year).ThenByDescending(d=>d.Month).ThenBy(d=>d.BranchName),
                        };
                    }
                    else
                    {
                        newqueryableDonations = orderBy switch
                        {
                            "count" => newqueryableDonations.OrderByDescending(d => d.Count).ThenByDescending(d => d.Year).ThenByDescending(d => d.Month),

                            "totalWeightInPounds" => newqueryableDonations.OrderByDescending(d => d.TotalWeightInPounds).ThenByDescending(d => d.Year).ThenByDescending(d => d.Month),

                            "province" => newqueryableDonations.OrderByDescending(d => d.Province).ThenByDescending(d => d.Year).ThenByDescending(d => d.Month),

                            "branchName" => newqueryableDonations.OrderByDescending(d => d.BranchName).ThenByDescending(d => d.Year).ThenByDescending(d => d.Month),

                            "month" => newqueryableDonations.OrderByDescending(d => d.Month).ThenByDescending(d => d.Year),

                            "year" => newqueryableDonations.OrderByDescending(d => d.Year).ThenByDescending(d => d.Month).ThenBy(d => d.BranchName),

                            _ => newqueryableDonations.OrderByDescending(d => d.Year).ThenByDescending(d => d.Month).ThenByDescending(d => d.BranchName),

                        };
                    }
                }

                var totalCount = await newqueryableDonations.CountAsync();

                if (offset.HasValue && perPage.HasValue)
                {
                    newqueryableDonations = newqueryableDonations
                        .Skip(offset.Value)
                        .Take(perPage.Value);
                }

                var resultList = await newqueryableDonations.ToListAsync();
                var pagedDonations = new DTO.Responses.PagedEntity<DTO.Responses.DonationByBranch>()
                {
                    TotalCount = totalCount,
                    Entities = resultList
                };

                return Ok(pagedDonations);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = "cannot edit donation" });
            }
        }

        [HttpGet("pagedaggregatedbystore")]
        public async Task<ActionResult<DTO.Responses.PagedEntity<DTO.Responses.DonationByStore>>> GetPagedDonationsAggregatedByStoreAsync(
            [FromQuery] bool includeDeleted = false,
            [FromQuery] string appSource = "plugin",
            [FromQuery] string searchQuery = null,
            [FromQuery] string orderBy = null,
            [FromQuery] string order = null,
            [FromQuery] int? offset = null,
            [FromQuery] int? perPage = null,
            [FromQuery] int? year = null,
            [FromQuery] int? month = null,
            [FromQuery] Guid? branchId = null,
            [FromQuery] Guid? bannerId = null,
            [FromQuery] Guid? retailerId = null,
            [FromQuery] Guid? storeId = null,
            [FromQuery] DateTime? startDate = null,
            [FromQuery] DateTime? endDate = null)
        {
            var queryableDonations = GetQueryableDonations(appSource);

            if (!includeDeleted)
            {
                queryableDonations = queryableDonations
                    .Where(d => !d.Deleted);
            }

            var useFilteredCount = !string.IsNullOrWhiteSpace(searchQuery) || year.HasValue ||
                month.HasValue || branchId.HasValue || bannerId.HasValue || storeId.HasValue || retailerId.HasValue;

            // apply search query
            if (!string.IsNullOrWhiteSpace(searchQuery))
            {
                queryableDonations = queryableDonations
                    .Where(d => d.Store.Name.Contains(searchQuery, StringComparison.OrdinalIgnoreCase));
            }

            // apply filters
            if (startDate.HasValue && endDate.HasValue)
            {
                queryableDonations = queryableDonations
                    .Where(d => d.Date >= startDate && d.Date <= endDate);
            }

            if (year.HasValue)
            {
                queryableDonations = queryableDonations
                    .Where(d => d.Date != null && d.Date.Year == year.Value);
            }

            if (month.HasValue)
            {
                queryableDonations = queryableDonations
                    .Where(d => d.Date != null && d.Date.Month == month.Value);
            }

            if (branchId.HasValue)
            {
                queryableDonations = queryableDonations
                    .Where(d => d.Branch.Id == branchId.Value);
            }

            if (retailerId.HasValue)
            {
                queryableDonations = queryableDonations
                    .Where(d => d.Store.Banner.RetailerId == retailerId.Value);
            }

            if (bannerId.HasValue)
            {
                queryableDonations = queryableDonations
                    .Where(d => d.Store.Banner.Id == bannerId.Value);
            }

            if (storeId.HasValue)
            {
                queryableDonations = queryableDonations
                    .Where(d => d.Store.Id == storeId.Value);
            }

            try
            {

                var newqueryableDonations = queryableDonations
                        .GroupBy(c => new
                        {
                            c.StoreId,
                            c.Store.Name,
                            c.Store.Province,
                            c.Store.Number,
                            b = c.Store.Banner.Name,
                            r = c.Store.Retailer.Name,
                            branch = c.Branch.Name,
                        })
                       .Select(d => new DTO.Responses.DonationByStore
                       {
                           StoreId = d.Key.StoreId,
                           StoreName = d.Key.Name,
                           StoreNumber = d.Key.Number,
                           Province = d.Key.Province,
                           Count = d.Count(),                                                                                 
                           TotalWeightInPounds = d.Sum(w => w.WeightInPounds),
                           Year = 0,
                           Month = 0,
                           RetailerName = d.Key.r,
                           BannerName = d.Key.b,
                           BranchName = d.Key.branch
                       });

                if (!string.IsNullOrWhiteSpace(orderBy) && !string.IsNullOrWhiteSpace(order))
                {
                    if (order.Equals("asc", StringComparison.OrdinalIgnoreCase))
                    {
                        newqueryableDonations = orderBy switch
                        {
                            "count" => newqueryableDonations.OrderBy(d => d.Count),

                            "totalWeightInPounds" => newqueryableDonations.OrderBy(d => d.TotalWeightInPounds),

                            "storeId" => newqueryableDonations.OrderBy(d => d.StoreId),

                            "province" => newqueryableDonations.OrderBy(d => d.Province),

                            "bannerName" => newqueryableDonations.OrderBy(d => d.BannerName),

                            "retailerName" => newqueryableDonations.OrderBy(d => d.RetailerName),

                            "branchName" => newqueryableDonations.OrderBy(d => d.BranchName),

                            _ => newqueryableDonations.OrderBy(d => d.StoreName),
                        };
                    }
                    else
                    {
                        newqueryableDonations = orderBy switch
                        {
                            "count" => newqueryableDonations.OrderByDescending(d => d.Count),

                            "totalWeightInPounds" => newqueryableDonations.OrderByDescending(d => d.TotalWeightInPounds),

                            "storeId" => newqueryableDonations.OrderByDescending(d => d.StoreId),

                            "province" => newqueryableDonations.OrderByDescending(d => d.Province),

                            "bannerName" => newqueryableDonations.OrderByDescending(d => d.BannerName),

                            "retailerName" => newqueryableDonations.OrderByDescending(d => d.RetailerName),

                            "branchName" => newqueryableDonations.OrderByDescending(d => d.BranchName),

                            _ => newqueryableDonations.OrderByDescending(d => d.StoreName),
                        };
                    }
                }

                var totalCount = await newqueryableDonations.CountAsync();
                
                if (offset.HasValue && perPage.HasValue)
                {
                    newqueryableDonations = newqueryableDonations
                        .Skip(offset.Value)
                        .Take(perPage.Value);
                }

                var resultList = await newqueryableDonations.ToListAsync();
                var pagedDonations = new DTO.Responses.PagedEntity<DTO.Responses.DonationByStore>()
                {
                    TotalCount = totalCount,// useFilteredCount ? filteredDonationsCount : totalDonationsCount,
                    Entities = resultList
                };

                return Ok(pagedDonations);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = "cannot edit donation" });
            }
        }

		// PagedEntity will include total count along with results
		// if we want to merge with above method, then need to update bot call because different response
		[HttpGet("paged")]
		public async Task<ActionResult<DTO.Responses.PagedEntity<DTO.Responses.Donation>>> GetPagedDonationsAsync(
			[FromQuery] bool includeDeleted = false,
			[FromQuery] string appSource = "bot",
			[FromQuery] string searchQuery = null,
			[FromQuery] string orderBy = null,
			[FromQuery] string order = null,
			[FromQuery] int? offset = null,
			[FromQuery] int? perPage = null,
			[FromQuery] bool trash = false,
            [FromQuery] Guid? retailerId = null,
            [FromQuery] Guid? bannerId = null,
            [FromQuery] string storeNumber = null,
            [FromQuery] Guid? branchId = null,
            [FromQuery] string branchProvince = null,
            [FromQuery] Guid? storeId = null,
            [FromQuery] DateTime? startDate = null,
            [FromQuery] DateTime? endDate = null)
		{
			var queryableDonations = GetQueryableDonations(appSource);

			// trash includes only deleted, includeDeleted includes all
			if (!includeDeleted && trash)
			{
                // only get trash results for past 30 days
                var utcNowDate = DateTime.UtcNow.Date;
                queryableDonations = queryableDonations
					.Where(d => d.Deleted && d.DeleteDate.Value.Date >= utcNowDate.AddDays(-30));
			}
			else if (!includeDeleted && !trash)
			{
				queryableDonations = queryableDonations
					.Where(d => !d.Deleted);
			}

            // apply search query
            if (!string.IsNullOrWhiteSpace(searchQuery))
            {
                queryableDonations = queryableDonations
                    .Where(d => d.Store.Name.Contains(searchQuery, StringComparison.OrdinalIgnoreCase) ||
                        d.Branch.Name.Contains(searchQuery, StringComparison.OrdinalIgnoreCase));
            }

            // apply filters
            if (retailerId.HasValue)
            {
                queryableDonations = queryableDonations
                    .Where(d => d.Store.RetailerId == retailerId.Value);
            }

            if (bannerId.HasValue)
            {
                queryableDonations = queryableDonations
                    .Where(d => d.Store.BannerId == bannerId.Value);
            }

            if (!string.IsNullOrWhiteSpace(storeNumber))
            {
                queryableDonations = queryableDonations
                    .Where(d => d.Store.Number.Equals(storeNumber, StringComparison.OrdinalIgnoreCase));
            }

            if (branchId.HasValue)
            {
                queryableDonations = queryableDonations
                    .Where(d => d.BranchId == branchId.Value);
            }

            if (!string.IsNullOrWhiteSpace(branchProvince))
            {
                queryableDonations = queryableDonations
                    .Where(d => d.Branch.Province.Equals(branchProvince, StringComparison.OrdinalIgnoreCase));
            }

            if (startDate.HasValue && endDate.HasValue)
            {
                queryableDonations = queryableDonations
                    .Where(d => d.Date >= startDate && d.Date <= endDate);
            }

            // order results
            if (!string.IsNullOrWhiteSpace(orderBy) && !string.IsNullOrWhiteSpace(order))
            {
                if (order.Equals("asc", StringComparison.OrdinalIgnoreCase))
                {
                    queryableDonations = orderBy switch
                    {
                        "retailerName" => queryableDonations.OrderBy(d => d.Store.Retailer.Name),

                        "bannerName" => queryableDonations.OrderBy(d => d.Store.Banner.Name),

                        "storeNumber" => queryableDonations.OrderBy(d => d.Store.Number),

                        "storeName" => queryableDonations.OrderBy(d => d.Store.Name),

                        "branchName" => queryableDonations.OrderBy(d => d.Branch.Name),

                        "branchProvince" => queryableDonations.OrderBy(d => d.Branch.Province),

                        "weightInPounds" => queryableDonations.OrderBy(d => d.WeightInPounds),

                        "creationDate" => queryableDonations.OrderBy(d => d.CreationDate),

                        _ => queryableDonations.OrderBy(d => d.Date),
                    };
                }
                else
                {
                    queryableDonations = orderBy switch
                    {
                        "retailerName" => queryableDonations.OrderByDescending(d => d.Store.Retailer.Name),

                        "bannerName" => queryableDonations.OrderByDescending(d => d.Store.Banner.Name),

                        "storeNumber" => queryableDonations.OrderByDescending(d => d.Store.Number),

                        "storeName" => queryableDonations.OrderByDescending(d => d.Store.Name),

                        "branchName" => queryableDonations.OrderByDescending(d => d.Branch.Name),

                        "branchProvince" => queryableDonations.OrderByDescending(d => d.Branch.Province),

                        "weightInPounds" => queryableDonations.OrderByDescending(d => d.WeightInPounds),

                        "creationDate" => queryableDonations.OrderByDescending(d => d.CreationDate),

                        _ => queryableDonations.OrderByDescending(d => d.Date),
                    };
                }
            }

            var donationsCount = await queryableDonations.CountAsync();

            // paginate final results
            if (offset.HasValue && perPage.HasValue)
            {
                queryableDonations = queryableDonations
                    .Skip(offset.Value)
                    .Take(perPage.Value);
            }

            var donations = await queryableDonations.ToListAsync();

            var pagedDonations = new DTO.Responses.PagedEntity<DTO.Responses.Donation>()
            {
                TotalCount = donationsCount,
                Entities = donations.Select(d => mapper.Map<Domain.Donation, DTO.Responses.Donation>(d)).ToList(),
            };

            foreach (var entity in pagedDonations.Entities)
            {
                if (entity.CreatedBy != null)
                {
                    var user = await context.Users.FirstOrDefaultAsync(d => d.Id == entity.CreatedBy);
                    if (user != null)
                    {
                        entity.CreatedBy = user.UserName;
                    }
                }

                if (entity.LastModifiedBy != null)
                {
                    var user = await context.Users.FirstOrDefaultAsync(d => d.Id == entity.LastModifiedBy);
                    if (user != null)
                    {
                        entity.LastModifiedByUsername = user.UserName;
                    }
                }
            }

            return Ok(pagedDonations);
        }

		[HttpGet("counts")]
		public async Task<ActionResult<DTO.Responses.EntityCounts>> GetDonationsCountsAsync()
		{
            var utcNowDate = DateTime.UtcNow.Date;
            
            var donationsCounts = new DTO.Responses.EntityCounts()
			{
				Total = await context.Donations.CountAsync(d => !d.Deleted),
                // only get trash counts for past 30 days
                Deleted = await context.Donations
                    .CountAsync(d => d.Deleted && d.DeleteDate.Value.Date >= utcNowDate.AddDays(-30)),
			};

			return Ok(donationsCounts);
		}

        [HttpGet("donationyears")]
        public async Task<ActionResult<List<int>>> GetDonationsYearsAsync([FromQuery] string appSource = "plugin")
        {
            var resultList = GetQueryableDonations(appSource).Where(d => !d.Deleted && d.Date != null).Select(d => d.Date.Year).Distinct().ToList().OrderByDescending(d => d);

            return Ok(resultList);
        }

        // allow plugin to update even if donation is submitted and all branch donations 
        // in given year and month are submitted
        [Authorize]
		[ClaimsFilter]
		[HttpPut("{id}")]
		public async Task<ActionResult<DTO.Responses.Donation>> UpdateDonationAsync(
			string userId, 
            Guid id, 
			[FromBody] DTO.Requests.Donation body,
			[FromQuery] string appSource = "bot")
		{
            if (appSource.Equals("bot") && await IsMonthSubmitted(body.BranchId, body.Date.Year, body.Date.Month))
            {
                return BadRequest(new { message = "month already submitted" });
            }

            var donation = await context.Donations
                .Include(d => d.Store)
                .Include(d => d.Categories)
                .ThenInclude(d => d.Category)
                .FirstOrDefaultAsync(d => d.Id == id);

            if (donation == null)
            {
                return NotFound();
            }

            donation.UpdateStore(body.StoreId, userId, appSource);
            donation.UpdateDate(body.Date, userId, appSource);
            donation.UpdateWeight(body.Weight, body.WeightUnit, userId, appSource);

            try
            {
                donation.UpdateCategories(body.Categories.ToDictionary(k => k.CategoryId, v => v.Fraction), userId, appSource);
            }
            catch
            {
                return BadRequest(new { message = "cannot edit donation" });
            }

            await context.SaveChangesAsync();

            return Ok(mapper.Map<Domain.Donation, DTO.Responses.Donation>(donation));
        }

        [Authorize]
		[ClaimsFilter]
		[HttpDelete("{id}")]
		public async Task<ActionResult> DeleteDonationAsync(
			string userId, Guid id, 
			[FromQuery] bool softDelete = true,
			[FromQuery] string appSource = "bot")
		{
			var donation = await context.Donations.FindAsync(id);

            if (donation == null)
            {
                return NotFound();
            }

			if (softDelete)
			{
				donation.Delete(userId, appSource);

                await context.SaveChangesAsync();
            }
            else
            {
                context.Donations.Remove(donation);

                await context.SaveChangesAsync();
            }

            return Ok();
        }

        [Authorize]
        [ClaimsFilter]
        [HttpPost("bulk-delete")]
        public async Task<ActionResult> DeleteDonationsAsync(string userId, [FromBody] Guid[] ids, [FromQuery] bool softDelete = true, [FromQuery] string appSource = "bot")
        {
            foreach (var id in ids)
            {
                var donation = await context.Donations.FindAsync(id);

                if (donation != null)
                {
                    if (softDelete)
                    {
                        donation.Delete(userId, appSource);
                    }
                    else
                    {
                        context.Donations.Remove(donation);
                    }
                }
            }

            await context.SaveChangesAsync();

            return Ok();
        }

        [Authorize]
        [ClaimsFilter]
        [HttpPut("restore/{id}")]
        public async Task<ActionResult> RestoreDonationAsync(string userId, Guid id)
        {
            var donation = await context.Donations.FindAsync(id);

            if (donation == null)
            {
                return NotFound();
            }

            donation.Restore(userId);

            await context.SaveChangesAsync();

            return Ok(mapper.Map<Domain.Donation, DTO.Responses.Donation>(donation));
        }

        [Authorize]
        [ClaimsFilter]
        [HttpPut("bulk-restore")]
        public async Task<ActionResult> RestoreDonationsAsync(string userId, [FromBody] Guid[] ids)
        {
            foreach (var id in ids)
            {
                var donation = await context.Donations.FindAsync(id);

                if (donation != null)
                {
                    donation.Restore(userId);
                }
            }

            await context.SaveChangesAsync();

            return Ok();
        }

        [Authorize]
        [ClaimsFilter]
        [HttpPost("duplicate/{id}")]
        public async Task<ActionResult<DTO.Responses.Donation>> DuplicateDonationAsync(string userId, Guid id)
        {
            var donation = await context.Donations
                        .Include(d => d.Store)
                        .Include(d => d.Categories)
                        .FirstOrDefaultAsync(d => d.Id == id);

            if (donation == null)
            {
                return NotFound();
            }

            var duplicateDonation = new Domain.Donation(donation.BranchId,
                donation.StoreId,
                donation.Date,
                donation.Weight,
                (FBC.Domain.WeightUnit)donation.WeightUnit,
                userId);


            if (donation.Submitted)
            {
                duplicateDonation.DuplicateSubmit(donation, userId);
            }

            if (donation.Categories != null && donation.Categories.Count() > 0)
            {
                try
                {
                    duplicateDonation.UpdateCategories(donation.Categories.ToDictionary(k => k.CategoryId, v => v.Fraction), userId, "plugin");
                }
                catch
                {
                    return BadRequest();
                }
            }

            context.Donations.Add(duplicateDonation);

            await context.SaveChangesAsync();

            return Ok(mapper.Map<Domain.Donation, DTO.Responses.Donation>(duplicateDonation));
        }

        private bool WithinDateRange(DateTime d, string daterange)
        {
            //daterange yyyy-mm-dd to yyyy-mm-dd
            var splitString = " to ";
            var dateList = daterange.Split(splitString);
            if (dateList != null && dateList.Length == 2)
            {

                DateTime startDate;
                DateTime endDate;
                if (DateTime.TryParseExact(dateList[0], "yyyy-MM-dd", null, DateTimeStyles.None, out startDate)
                    && DateTime.TryParseExact(dateList[1], "yyyy-MM-dd", null, DateTimeStyles.None, out endDate))
                {
                    if (d > startDate && d < endDate)
                    {
                        return true;
                    }
                }                
            }
            
            return false;            
        }

        private DateTime? GetStartDate (string daterange)
        {
            //daterange yyyy-mm-dd to yyyy-mm-dd
            var splitString = " to ";
            var dateList = daterange.Split(splitString);
            if (dateList != null && dateList.Length == 2)
            {

                DateTime startDate;                
                if (DateTime.TryParseExact(dateList[0], "yyyy-MM-dd", null, DateTimeStyles.None, out startDate))
                {
                    return startDate;
                }
            }

            return null;
        }

        private DateTime? GetEndDate(string daterange)
        {
            //daterange yyyy-mm-dd to yyyy-mm-dd
            var splitString = " to ";
            var dateList = daterange.Split(splitString);
            if (dateList != null && dateList.Length == 2)
            {

                DateTime endDate;
                if (DateTime.TryParseExact(dateList[1], "yyyy-MM-dd", null, DateTimeStyles.None, out endDate))
                {
                    return endDate;
                }
            }

            return null;
        }

        private bool WithinDateRanges(DateTime d, List<string> dateranges)
        {            
            foreach (var daterange in dateranges)
            {
                if (WithinDateRange(d, daterange))
                {
                    return true;
                }
            }
            return false;
        }

        public class StoreDonation
        {
            // Auto-implemented properties.
            public Store Store { get; set; }
            public IEnumerable<Donation> Donations{ get; set; }

            public StoreDonation()
            {
            }

            public StoreDonation(Store store, IEnumerable<Donation> donations)
            {
                this.Store = store;
                this.Donations = donations;
            }
        }

        [Authorize]
        [ClaimsFilter]
        [HttpPost("export-internal-donation-data")]
        public async Task<ActionResult<IList<DTO.Responses.PluginDonationExport>>> GenerateInternalDonationData(
            [FromBody] DTO.Requests.PluginDonationExportOptions body, 
            [FromQuery] bool includeDeletedStores = false)
        {
            var exportYear = body.Year;
            var exportMonths = body.Months;
            if (exportMonths.Count() == 0)
            {
                for (var i = 1; i <= 12; i++)
                {
                    exportMonths.Add(i);
                }
            }

            // branch submission status
            var branchSubmissions = context.DonationSubmissions
                .Where(ds => ds.Year == exportYear && ds.Submitted)
                .AsEnumerable()
                .GroupBy(dsg => dsg.BranchId)
                .Select(dsg => new
                {
                    Branch = dsg.Key,
                    Submissions = dsg
                })
                .ToDictionary(x => x.Branch, x => x.Submissions.ToDictionary(y => $"{y.Year}-{y.Month}", y => y.Submitted));

            // donation categories
            var donationCategories = body.IncludeCategories ? 
                context.DonationCategories
                .Include(dc => dc.Category)
                .AsEnumerable()
                .GroupBy(dcg => dcg.DonationId)
                .Select(dcg => new
                {
                    DonationId = dcg.Key,
                    DonationCategories = dcg
                })
                .ToDictionary(x => x.DonationId, x => x.DonationCategories
                    .Select(dc => new DTO.Responses.PluginDonationExport.DonationCategory
                    {
                        CategoryId = dc.Category.Id,
                        Fraction = dc.Fraction,
                        CategoryName = dc.Category.Name,
                        DisplayOrder = dc.Category.DisplayOrder
                    })
                    .ToArray()
                ) : null;

            // stores & donations
            var storesDonations = await context.Stores
                .Include(s => s.Retailer)
                .Include(s => s.Banner)
                .Include(s => s.Branch)
                .ThenInclude(b => b.Donations)
                .Where(s => (includeDeletedStores || !s.Deleted) &&
                    (s.Retailer.Id == body.RetailerId) &&
                    (body.StoreIds.Count() == 0 || body.StoreIds.Contains(s.Id)) &&
                    (body.StoreProvinces.Count() == 0 || body.StoreProvinces.Contains(s.Province)))
                .Select(store => new
                {
                    Store = store,
                    // only include stores donations
                    Donations = store.Branch.Donations
                        .Where(d => !d.Deleted && d.StoreId == store.Id && d.Date.Year == exportYear && (exportMonths.Contains(d.Date.Month)))
                        .Select(d => new { d.Id, d.Date, d.WeightInPounds })
                })
                .OrderBy(sd => sd.Store.Number)
                .ToListAsync();

            // parse donations into monthly format
            var exportStoreDonations = new List<DTO.Responses.PluginDonationExport>();
            foreach (var sd in storesDonations)
            {
                var monthlyDonationsList = new List<DTO.Responses.PluginDonationExport.Donation>();
                foreach (var month in exportMonths)
                {
                    var donationsInMonth = sd.Donations.Where(d => d.Date.Month == month);
                    var monthlyDonations = new DTO.Responses.PluginDonationExport.Donation()
                    {
                        Month = month
                    };

                    if (branchSubmissions.ContainsKey(sd.Store.BranchId)
                        && branchSubmissions[sd.Store.BranchId].ContainsKey($"{exportYear}-{month}"))
                    {
                        monthlyDonations.Submitted = true;
                    }
                    else
                    {
                        monthlyDonations.Submitted = false;
                    }

                    if (donationsInMonth.Count() > 0)
                    {
                        monthlyDonations.TotalCount = donationsInMonth.Count();
                        monthlyDonations.TotalWeightInPounds = donationsInMonth.Sum(d => d.WeightInPounds);

                        if (body.AggregateByStore)
                        {
                            monthlyDonations.NonAggregatedWeightsAndCategories = null;

                            if (body.IncludeCategories)
                            {
                                // num donations with non 0/null categoies to calculate average
                                var numDonationsWithCategories = 0;
                                var categorySums = new Dictionary<Guid, DTO.Responses.PluginDonationExport.DonationCategory>();

                                foreach (var donation in donationsInMonth)
                                {
                                    if (donationCategories.ContainsKey(donation.Id))
                                    {
                                        // if donation has categories and has a non-null and non-0 category fraction
                                        if (donationCategories[donation.Id].Any(dc => dc.Fraction.HasValue && dc.Fraction.Value != 0))
                                        {
                                            numDonationsWithCategories++;
                                        }

                                        // loop through donations categories and sum fractions
                                        var categories = donationCategories[donation.Id];
                                        foreach (var category in categories)
                                        {
                                            if (category.Fraction.HasValue)
                                            {
                                                if (categorySums.ContainsKey(category.CategoryId))
                                                {
                                                    categorySums[category.CategoryId].Fraction += category.Fraction.Value;
                                                }
                                                else
                                                {
                                                    categorySums.Add(category.CategoryId, category);
                                                }
                                            }
                                            else
                                            {
                                                if (!categorySums.ContainsKey(category.CategoryId))
                                                {
                                                    categorySums.Add(category.CategoryId, new DTO.Responses.PluginDonationExport.DonationCategory()
                                                    {
                                                        CategoryId = category.CategoryId,
                                                        Fraction = 0,
                                                        CategoryName = category.CategoryName,
                                                        DisplayOrder = category.DisplayOrder
                                                    });
                                                }
                                            }
                                        }

                                    }
                                }

                                if (numDonationsWithCategories > 0)
                                {
                                    monthlyDonations.AggregatedDonationCategories = categorySums.Values.Select(c => c).ToArray();
                                    foreach (var c in monthlyDonations.AggregatedDonationCategories)
                                    {
                                        c.Fraction /= numDonationsWithCategories;
                                    }
                                }
                            }
                            
                        }
                        else
                        {
                            monthlyDonations.AggregatedDonationCategories = null;
                            monthlyDonations.NonAggregatedWeightsAndCategories = donationsInMonth
                                .OrderBy(d => d.Date)
                                .Select(d => new DTO.Responses.PluginDonationExport.NonAggregatedData {
                                    Weight = d.WeightInPounds,
                                    Date = d.Date,
                                    DonationCategories = body.IncludeCategories && donationCategories.ContainsKey(d.Id) ? donationCategories[d.Id] : null
                                }).ToArray();
                        }
                    }
                    else
                    {
                        monthlyDonations.TotalCount = 0;
                        monthlyDonations.TotalWeightInPounds = 0;
                        monthlyDonations.NonAggregatedWeightsAndCategories = null;
                    }

                    monthlyDonationsList.Add(monthlyDonations);
                }

                var storeDonation = new DTO.Responses.PluginDonationExport()
                {
                    StoreNumber = sd.Store.Number,
                    StoreName = sd.Store.Name,
                    StoreProvince = sd.Store.Province, // use store province
                    RetailerName = sd.Store.Retailer.Name,
                    BannerName = body.IncludeBanner ? sd.Store.Banner?.Name : null,
                    BranchName = sd.Store.Branch.Name,
                    Donations = monthlyDonationsList.ToArray()
                };

                exportStoreDonations.Add(storeDonation);
            }

            return Ok(exportStoreDonations);
        }

        [Authorize]
        [ClaimsFilter]
        [HttpPost("export-retailer-donation-data")]
        public async Task<ActionResult<IList<DTO.Responses.PluginDonationExport>>> GenerateRetailerDonationData(
            [FromBody] DTO.Requests.PluginDonationExportOptions body,
            [FromQuery] bool includeDeletedStores = false)
        {
            var dateSet = new HashSet<DateTime>();
            foreach (var dateRange in body.DateRanges)
            {
                var startDate = GetStartDate(dateRange);
                var endDate = GetEndDate(dateRange);

                if (startDate.HasValue && endDate.HasValue)
                {
                    for (var dt = startDate.Value; dt <= endDate.Value; dt = dt.AddDays(1))
                    {
                        dateSet.Add(dt);
                    }
                }
            }

            var dateList = dateSet.ToList();

            try
            {
                // donation categories
                var donationCategories = body.IncludeCategories ?
                    context.DonationCategories
                    .Include(dc => dc.Category)
                    .AsEnumerable()
                    .GroupBy(dcg => dcg.DonationId)
                    .Select(dcg => new
                    {
                        DonationId = dcg.Key,
                        DonationCategories = dcg
                    })
                    .ToDictionary(x => x.DonationId, x => x.DonationCategories
                        .Select(dc => new DTO.Responses.PluginDonationExport.DonationCategory
                        {
                            CategoryId = dc.Category.Id,
                            Fraction = dc.Fraction,
                            CategoryName = dc.Category.Name,
                            DisplayOrder = dc.Category.DisplayOrder
                        })
                        .ToArray()
                    ) : null;

                var storesDonations = await context.Stores
                .Include(s => s.Retailer)
                .Include(s => s.Banner)
                .Include(s => s.Branch)
                .ThenInclude(s => s.Donations)
                .Where(s => (includeDeletedStores || !s.Deleted) &&
                    (s.Retailer.Id == body.RetailerId) &&
                    (body.StoreIds.Count() == 0 || body.StoreIds.Contains(s.Id)) &&
                    (body.StoreProvinces.Count() == 0 || body.StoreProvinces.Contains(s.Province)))
                .Select(store => new
                StoreDonation
                {
                    Store = store,
                    // only include stores donations
                    Donations = store.Branch.Donations
                        .Where(d => !d.Deleted && d.StoreId == store.Id && dateList.Contains(d.Date.Date))
                })
                .OrderBy(sd => sd.Store.Number)
                .ToListAsync();


                // parse donations
                var exportStoreDonations = new List<DTO.Responses.PluginDonationExport>();
                foreach (var sd in storesDonations)
                {
                    var donationsList = new List<DTO.Responses.PluginDonationExport.Donation>();
                    foreach (var dateRange in body.DateRanges)
                    {
                        var startDate = GetStartDate(dateRange);
                        var endDate = GetEndDate(dateRange);

                        if (startDate.HasValue && endDate.HasValue)
                        {
                            var donationsInRange = sd.Donations.Where(d => d.Date >= startDate && d.Date <= endDate);
                            var exportDonationsInRange = new DTO.Responses.PluginDonationExport.Donation()
                            {
                                DateRange = dateRange
                            };

                            if (donationsInRange.Count() > 0)
                            {
                                exportDonationsInRange.Submitted = true;
                                exportDonationsInRange.TotalCount = donationsInRange.Count();
                                exportDonationsInRange.TotalWeightInPounds = donationsInRange.Sum(d => d.WeightInPounds);

                                if (body.AggregateByStore)
                                {
                                    exportDonationsInRange.NonAggregatedWeightsAndCategories = null;

                                    if (body.IncludeCategories)
                                    {
                                        // num donations with non 0/null categoies to calculate average
                                        var numDonationsWithCategories = 0;
                                        var categorySums = new Dictionary<Guid, DTO.Responses.PluginDonationExport.DonationCategory>();

                                        foreach (var donation in donationsInRange)
                                        {
                                            if (donationCategories.ContainsKey(donation.Id))
                                            {
                                                // if donation has categories and has a non-null and non-0 category fraction
                                                if (donationCategories[donation.Id].Any(dc => dc.Fraction.HasValue && dc.Fraction.Value != 0))
                                                {
                                                    numDonationsWithCategories++;
                                                }

                                                // loop through donations categories and sum fractions
                                                var categories = donationCategories[donation.Id];
                                                foreach (var category in categories)
                                                {
                                                    if (category.Fraction.HasValue)
                                                    {
                                                        if (categorySums.ContainsKey(category.CategoryId))
                                                        {
                                                            categorySums[category.CategoryId].Fraction += category.Fraction.Value;
                                                        }
                                                        else
                                                        {
                                                            categorySums.Add(category.CategoryId, category);
                                                        }
                                                    }
                                                    else
                                                    {
                                                        if (!categorySums.ContainsKey(category.CategoryId))
                                                        {
                                                            categorySums.Add(category.CategoryId, new DTO.Responses.PluginDonationExport.DonationCategory()
                                                            {
                                                                CategoryId = category.CategoryId,
                                                                Fraction = 0,
                                                                CategoryName = category.CategoryName,
                                                                DisplayOrder = category.DisplayOrder
                                                            });
                                                        }
                                                    }
                                                }

                                            }
                                        }

                                        if (numDonationsWithCategories > 0)
                                        {
                                            exportDonationsInRange.AggregatedDonationCategories = categorySums.Values.Select(c => c).ToArray();
                                            foreach (var c in exportDonationsInRange.AggregatedDonationCategories)
                                            {
                                                c.Fraction /= numDonationsWithCategories;
                                            }
                                        }
                                    }

                                }
                                else
                                {
                                    exportDonationsInRange.AggregatedDonationCategories = null;
                                    exportDonationsInRange.NonAggregatedWeightsAndCategories = donationsInRange
                                        .OrderBy(d => d.Date)
                                        .Select(d => new DTO.Responses.PluginDonationExport.NonAggregatedData
                                        {
                                            Weight = d.WeightInPounds,
                                            Date = d.Date,
                                            DonationCategories = body.IncludeCategories && donationCategories.ContainsKey(d.Id) ? donationCategories[d.Id] : null
                                        }).ToArray();
                                }
                            }
                            else
                            {
                                exportDonationsInRange.Submitted = false;
                                exportDonationsInRange.TotalCount = 0;
                                exportDonationsInRange.TotalWeightInPounds = 0;
                                exportDonationsInRange.NonAggregatedWeightsAndCategories = null;
                            }

                            donationsList.Add(exportDonationsInRange);
                        }
                    }

                    var storeDonation = new DTO.Responses.PluginDonationExport()
                    {
                        StoreNumber = sd.Store.Number,
                        StoreName = sd.Store.Name,
                        StoreProvince = sd.Store.Province, // use store province
                        RetailerName = sd.Store.Retailer.Name,
                        BannerName = body.IncludeBanner ? sd.Store.Banner?.Name : null,
                        BranchName = sd.Store.Branch.Name,
                        Donations = donationsList.ToArray()
                    };

                    exportStoreDonations.Add(storeDonation);
                }

                return Ok(exportStoreDonations);

            }
            catch (Exception)
            {
                var exportStoreDonations = new List<DTO.Responses.PluginDonationExport>();
                return Ok(exportStoreDonations);
            }

        }

        // include different entities depending on caller
        private IQueryable<Domain.Donation> GetQueryableDonations(string appSource)
        {
            return appSource switch
            {
                "bot" => context.Donations
                        .Include(d => d.Store)
                        .ThenInclude(s => s.Banner)
                        .Include(d => d.Store)
                        .ThenInclude(r => r.Retailer)
                        .Include(d => d.Categories)
                        .ThenInclude(dc => dc.Category),

                "plugin" => context.Donations
                            .Include(d => d.Branch)
                            .ThenInclude(ds => ds.DonationSubmissions)
                            .Include(d => d.Store)
                            .ThenInclude(s => s.Banner)
                            .Include(d => d.Store)
                            .ThenInclude(r => r.Retailer)
                            .Include(d => d.Categories),

                _ => context.Donations
            };
        }

        private IQueryable<Domain.DonationSubmission> GetQueryableDonationsByBranch(string appSource)
        {
            return appSource switch
            {
                "bot" => context.DonationSubmissions
                        .Include(ds => ds.Branch)
                        .ThenInclude(d => d.Donations)
                        .ThenInclude(d => d.Store)
                        .Include(ds => ds.Branch)
                        .ThenInclude(d => d.Donations)
                        .ThenInclude(d => d.Categories)
                        .ThenInclude(dc => dc.Category),

                "plugin" => context.DonationSubmissions
                            .Include(b => b.Branch)
                            .ThenInclude(d => d.Donations)
                            .ThenInclude(d => d.Store)
                            .ThenInclude(s => s.Banner)
                            .ThenInclude(r => r.Retailer)
                            .Include(ds => ds.Branch)
                            .ThenInclude(d => d.Donations)
                            .ThenInclude(c => c.Categories),

                _ => context.DonationSubmissions
            };
        }

        private async Task<bool> IsMonthSubmitted(Guid branchId, int year, int month)
        {
            var donationMonthSubmission = await context.DonationSubmissions.FirstOrDefaultAsync(ds => ds.BranchId == branchId && ds.Year == year && ds.Month == month);

            return donationMonthSubmission != null ? donationMonthSubmission.Submitted : false;
        }
    }
}
