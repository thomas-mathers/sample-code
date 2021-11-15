using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using AutoMapper;
using BSC.OrderTool.Domain;
using BSC.OrderTool.Web.DTO;
using BSC.OrderTool.Web.Extensions;
using BSC.OrderTool.Web.Middleware;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace BSC.OrderTool.Web.Controllers
{
    [ApiExceptionFilter]
    [Produces("application/json")]
    [Route(template: "api/order")]
    public class OrderController : Controller
    {
        private readonly IConfiguration config;
        private readonly BscDbContext context;
        private readonly IMapper mapper;

        public OrderController(IConfiguration configuration, BscDbContext context, IMapper mapper)
        {
            ;
            this.config = configuration;
            this.context = context;
            this.mapper = mapper;
        }

        [HttpGet]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [ClaimsFilter]
        [Route("{id}")]
        public async Task<dynamic> Get(Guid id)
        {
            var item = await context.Orders
                .Include(t => t.Site)
                .Include(t => t.LineItems)
                .Include(t => t.Account)
                .Include(t => t.PurchaseJobOrder)
                .ThenInclude(s => s.PurchaseJob)
                .Include(t => t.PurchaseJobOrder)
                .ThenInclude(s => s.CreatedBy)
                .FirstOrDefaultAsync(t => t.Id == id && !t.Deleted);

            if (item == null)
            {
                return NotFound("id");
            }

            var result = mapper.Map<Domain.Order, DTO.OrderDetail>(item);

            return Ok(result);
        }

        [HttpPut]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = "admin")]
        [ClaimsFilter]
        [Route("{id}")]
        public async Task<dynamic> Put(string userId, Guid id, [FromBody] DTO.OrderDetail order)
        {
            var item = await context.Orders
                .FirstOrDefaultAsync(t => t.Id == id && !t.Deleted);

            if (item == null)
            {
                return NotFound("id");
            }

            mapper.Map(order, item);
            item.Update(userId);

            await context.SaveChangesAsync();

            var response = mapper.Map<Domain.Order, DTO.OrderDetail>(item);
            return Ok(response);
        }

        [HttpDelete]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = "admin")]
        [ClaimsFilter]
        [Route("{id}")]
        public async Task<dynamic> Delete(string userId, Guid id)
        {
            var item = await context.Orders
                .FirstOrDefaultAsync(t => t.Id == id && !t.Deleted);

            if (item == null)
            {
                return NotFound("id");
            }

            item.Delete(userId);
            await context.SaveChangesAsync();

            return Ok();
        }
        [HttpGet]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [ClaimsFilter]
        [Route("payments")]
        public async Task<dynamic> Get()
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            var payments = context.Orders.Select(t => t.PaymentInfo).Distinct().ToList();


            return Ok(payments);
        }

        [HttpGet]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [ClaimsFilter]
        public async Task<dynamic> Get(
            [FromQuery] string siteId,
            [FromQuery] Guid? accountId,
            [FromQuery] string status,
            [FromQuery] string text,
            [FromQuery] string payment,
            [FromQuery] DateTime? dateFrom = null,
            [FromQuery] DateTime? dateTo = null,
            [FromQuery] string sortBy = "orderDate",
            [FromQuery] string sortDir = "desc",
            [FromQuery] int pageIndex = 0,
            [FromQuery] int pageSize = 100)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            bool totalWarning = false;

            var items = context.OrderDetails
                .Include(t => t.Order)
                .ThenInclude(t => t.LineItems)
                .Include(t => t.Order)
                .ThenInclude(t => t.Site)
                .Include(t => t.Order)
                .ThenInclude(t => t.Account)
                .Include(t => t.Order)
                .ThenInclude(t => t.PurchaseJobOrder)
                .ThenInclude(t => t.CreatedBy)
                .Where(t => !t.Order.Deleted);

            if (!string.IsNullOrWhiteSpace(siteId))
            {
                items = items.Where(t => t.Order.SiteId == siteId);
            }
            if (accountId.HasValue)
            {
                items = items.Where(t => t.Order.AccountId == accountId.Value);
            }
            if (!string.IsNullOrWhiteSpace(status))
            {
                if (Enum.TryParse(status, out OrderStatus s))
                {
                    items = items.Where(t => t.Status == s);
                }
                totalWarning = true;
            }
            if (!string.IsNullOrWhiteSpace(text))
            {
                var lower = text.ToLower();
                items = items.Where(t => t.Name.ToLower().Contains(lower) || t.SKU.ToLower().Contains(lower) || t.Order.OrderNumber.ToLower().Contains(lower));
                totalWarning = true;
            }
            if (!string.IsNullOrWhiteSpace(payment))
            {
                items = items.Where(t => t.Order.PaymentInfo == payment);
            }
            if (!dateFrom.HasValue)
            {
                dateFrom = DateTime.UtcNow.AddMonths(-1);
            }
            if (!dateTo.HasValue)
            {
                dateTo = DateTime.UtcNow.AddDays(1);
            }

            items = items.Where(t => t.Order.OrderDate >= dateFrom.Value && t.Order.OrderDate <= dateTo.Value);

            switch (sortBy)
            {
                case "account":
                    items = sortDir == "desc" ? items.OrderByDescending(t => t.Order.Account.UserName).ThenByDescending(t => t.Order.OrderNumber)
                        : items.OrderBy(t => t.Order.Account.UserName).ThenBy(t => t.Order.OrderNumber);
                    break;
                case "orderNumber":
                    items = sortDir == "desc" ? items.OrderByDescending(t => t.Order.OrderNumber) : items.OrderBy(t => t.Order.OrderNumber);
                    break;
                case "site":
                    items = sortDir == "desc" ? items.OrderByDescending(t => t.Order.Site.Id).ThenByDescending(t => t.Order.OrderNumber) :
                        items.OrderBy(t => t.Order.Site.Id).ThenBy(t => t.Order.OrderNumber);
                    break;
                case "name":
                    items = sortDir == "desc" ? items.OrderByDescending(t => t.Name) : items.OrderBy(t => t.Name);
                    totalWarning = true;
                    break;
                case "lastModifiedDateTime":
                    items = sortDir == "desc" ? items.OrderByDescending(t => t.Order.LastModifiedDate) : items.OrderBy(t => t.Order.LastModifiedDate);
                    break;
				case "shipmentDate":
                    items = sortDir == "desc" ? items.OrderByDescending(t => t.ShipmentDate) : items.OrderBy(t => t.ShipmentDate);
                    break;
                case "orderDate":
                default:
                    items = sortDir == "desc" ? items.OrderByDescending(t => t.Order.OrderDate).ThenByDescending(t => t.Order.LastModifiedDate) : items.OrderBy(t => t.Order.OrderDate).ThenBy(t => t.Order.LastModifiedDate);
                    break;
            }

            var count = items.Count();

            items = items.Skip(pageIndex * pageSize).Take(pageSize);

            var data = await items.ToListAsync();
            var results = new List<DTO.OrderSummary>();
            foreach (var lineItem in data)
            {
                var order = lineItem.Order;
                var result = mapper.Map<DTO.OrderSummary>(order);
                result.Id = lineItem.Id;
                result.AccountName = order.Account?.UserName ?? "Guest";
                result.SiteName = order.Site.Id;
                result.PurchaseCreatedByUser = order.PurchaseJobOrder?.CreatedBy?.UserName;
                result.Amount = lineItem.Amount;
                result.Name = lineItem.Name;
                result.Price = lineItem.Price;
                result.Total = lineItem.Total;
                result.OrderTax = order.Tax;
                result.OrderSubTotal = order.Total - order.Tax;
                result.OrderTotal = order.Total;
                result.Status = lineItem.Status;
                result.StatusString = lineItem.StatusString;
                result.TrackingNumber = lineItem.TrackingNumber;
                result.TrackingUrl = lineItem.TrackingUrl;
                result.LastModifiedDateTime = lineItem.LastModifiedDateTime;
                result.LastCheckedDateTime = lineItem.LastCheckedDateTime;
                result.Transactions = order.Transactions;
                result.ShipmentDate = lineItem.ShipmentDate;

                results.Add(result);
            }


            return Ok(new PaginatedResponse<DTO.OrderSummary>(results, pageSize, pageIndex, count, new { TotalWarning = totalWarning }));
        }


        [HttpGet]
        [Route("export")]
        public async Task<dynamic> Export(
               [FromQuery] string token,
               [FromQuery] string siteId,
               [FromQuery] Guid? accountId,
               [FromQuery] string status,
               [FromQuery] string text,
               [FromQuery] string payment,
               [FromQuery] DateTime dateFrom,
               [FromQuery] DateTime dateTo)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }


            var key = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(config["Tokens:Key"]));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            TokenValidationParameters validationParameters = new TokenValidationParameters()
            {
                ValidateIssuerSigningKey = true,
                ValidateAudience = false,
                ValidateIssuer = false,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(config["Tokens:Key"])),
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(0),
            };

            SecurityToken validatedToken;
            JwtSecurityTokenHandler handler = new JwtSecurityTokenHandler();
            var user = handler.ValidateToken(token, validationParameters, out validatedToken);

            var items = context.OrderDetails
                .Include(t => t.Order)
                .ThenInclude(t => t.LineItems)
                .Include(t => t.Order)
                .ThenInclude(t => t.Site)
                .Include(t => t.Order)
                .ThenInclude(t => t.Account)
                .Include(t => t.Order)
                .ThenInclude(t => t.PurchaseJobOrder)
                .ThenInclude(t => t.CreatedBy)
                .Where(t => !t.Order.Deleted && t.Order.OrderDate >= dateFrom && t.Order.OrderDate <= dateTo);

            if (!string.IsNullOrWhiteSpace(siteId))
            {
                items = items.Where(t => t.Order.SiteId == siteId);
            }
            if (accountId.HasValue)
            {
                items = items.Where(t => t.Order.AccountId == accountId.Value);
            }
            if (!string.IsNullOrWhiteSpace(status))
            {
                if (Enum.TryParse(status, out OrderStatus s))
                {
                    items = items.Where(t => t.Status == s);
                }
            }
            if (!string.IsNullOrWhiteSpace(text))
            {
                var lower = text.ToLower();
                items = items.Where(t => t.Name.ToLower().Contains(lower) || t.SKU.ToLower().Contains(lower) || t.Order.OrderNumber.ToLower().Contains(lower));
            }
            if (!string.IsNullOrWhiteSpace(payment))
            {
                items = items.Where(t => t.Order.PaymentInfo == payment);
            }
            items = items.OrderByDescending(t => t.Order.OrderDate).ThenByDescending(t => t.LastModifiedDateTime);

            var data = await items.ToListAsync();
            var results = new List<DTO.OrderSummary>();
            var orderNumber = "";
            foreach (var lineItem in data)
            {
                var include = false;
                var order = lineItem.Order;

                if (orderNumber != order.OrderNumber)
                {
                    orderNumber = order.OrderNumber;
                    include = true;
                }

                var result = mapper.Map<DTO.OrderSummary>(order);
                result.Id = lineItem.Id;
                result.AccountName = order.Account?.UserName ?? "Guest";
                result.SiteName = order.Site.Id;
                result.PurchaseCreatedByUser = order.PurchaseJobOrder?.CreatedBy?.UserName;
                result.Amount = lineItem.Amount;
                result.Name = lineItem.Name;
                result.Price = lineItem.Price;
                result.Total = lineItem.Total;
                if (include)
                {
                    result.OrderTax = order.Tax;
                    result.OrderSubTotal = order.Total - order.Tax;
                    result.OrderTotal = order.Total;
                    result.Transactions = order.Transactions;
                }
                else
                {
                    result.Transactions = "";
                }
                result.Status = lineItem.Status;
                result.StatusString = lineItem.StatusString;
                result.TrackingNumber = lineItem.TrackingNumber;
                result.TrackingUrl = lineItem.TrackingUrl;
                result.LastModifiedDateTime = lineItem.LastModifiedDateTime;
                result.LastCheckedDateTime = lineItem.LastCheckedDateTime;
                result.ShipmentDate = lineItem.ShipmentDate;

                results.Add(result);
            }

            var stream = new MemoryStream();

            using (var writer = new StreamWriter(stream, leaveOpen: true))
            {
                var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);
                var map = new OrderSummaryMap();

                csv.WriteRecords(results);
            }
            stream.Position = 0; //reset stream
            return File(stream, "application/octet-stream", "export.csv");
        }

        public sealed class OrderSummaryMap : ClassMap<OrderSummary>
        {
            public OrderSummaryMap()
            {
                AutoMap(CultureInfo.InvariantCulture);
                Map(m => m.Id).Ignore();
                Map(m => m.AccountId).Ignore();
                Map(m => m.InvoiceUrl).Ignore();
            }
        }


        [HttpGet]
        [Route("receipts")]
        public async Task<dynamic> Receipts(
       [FromQuery] string token,
       [FromQuery] string siteId,
       [FromQuery] Guid? accountId,
       [FromQuery] string status,
       [FromQuery] string text,
       [FromQuery] string payment,
       [FromQuery] DateTime dateFrom,
       [FromQuery] DateTime dateTo)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }


            var key = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(config["Tokens:Key"]));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            TokenValidationParameters validationParameters = new TokenValidationParameters()
            {
                ValidateIssuerSigningKey = true,
                ValidateAudience = false,
                ValidateIssuer = false,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(config["Tokens:Key"])),
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(0),
            };

            SecurityToken validatedToken;
            JwtSecurityTokenHandler handler = new JwtSecurityTokenHandler();
            var user = handler.ValidateToken(token, validationParameters, out validatedToken);

            var items = context.OrderDetails
                .Include(t => t.Order)
                .ThenInclude(t => t.LineItems)
                .Include(t => t.Order)
                .ThenInclude(t => t.Site)
                .Include(t => t.Order)
                .ThenInclude(t => t.Account)
                .Include(t => t.Order)
                .ThenInclude(t => t.PurchaseJobOrder)
                .ThenInclude(t => t.CreatedBy)
                .Where(t => !t.Order.Deleted && t.Order.OrderDate >= dateFrom && t.Order.OrderDate <= dateTo);

            if (!string.IsNullOrWhiteSpace(siteId))
            {
                items = items.Where(t => t.Order.SiteId == siteId);
            }
            if (accountId.HasValue)
            {
                items = items.Where(t => t.Order.AccountId == accountId.Value);
            }
            if (!string.IsNullOrWhiteSpace(status))
            {
                if (Enum.TryParse(status, out OrderStatus s))
                {
                    items = items.Where(t => t.Status == s);
                }
            }
            if (!string.IsNullOrWhiteSpace(text))
            {
                var lower = text.ToLower();
                items = items.Where(t => t.Name.ToLower().Contains(lower) || t.SKU.ToLower().Contains(lower) || t.Order.OrderNumber.ToLower().Contains(lower));
            }
            if (!string.IsNullOrWhiteSpace(payment))
            {
                items = items.Where(t => t.Order.PaymentInfo == payment);
            }
            items = items.Where(t => t.Order.InvoiceUrl != null);
            var orders = await items.Select(t => t.Order).Distinct().ToListAsync();

            var outStream = new MemoryStream();
            using (var archive = new ZipArchive(outStream, ZipArchiveMode.Create, true))
            {
                foreach (var order in orders)
                {
                    var fileInArchive = archive.CreateEntry(order.SiteId + "/" + order.Account.UserName + "/" + order.OrderNumber + ".png", CompressionLevel.Optimal);
                    var req = HttpWebRequest.Create(order.InvoiceUrl);
                    using (var entryStream = fileInArchive.Open())
                    using (WebResponse response = req.GetResponse())
                    {
                        using (var fileToCompressStream = response.GetResponseStream())
                        {
                            fileToCompressStream.CopyTo(entryStream);
                        }
                    }
                }
            }
            outStream.Position = 0; //reset stream
            return File(outStream, "application/octet-stream", "receipts.zip");
        }
    }
}
