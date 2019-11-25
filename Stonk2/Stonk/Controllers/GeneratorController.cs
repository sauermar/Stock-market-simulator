using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stonk.Models;
using Stonk.Models.Persons;
using Stonk.Plugins.Database;
using Stonks.Plugins.Generator;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;

namespace Stonks.Controllers
{
    [Route("[controller]/[action]")]
    public class GeneratorController : Controller
    {
        private readonly Database _context;




        private ulong Ticks { get; set; } = 0;

        List<Stock> LastIteration { get; set; }   // synchnize
        decimal TaxRate { get; set; }
        List<StockDependency> Dependencies { get; set; }
        int IterationsPerTicks { get; set; }
        //List<Stock>  { get; set; }

        ConcurrentDictionary<int, int> transactions = new ConcurrentDictionary<int, int> ();
        private readonly object _lock = new object();

        
        public GeneratorController(Database context)
        {
            _context = context;

            LastIteration = context.Stock.ToList();
            Dependencies = new List<StockDependency>();  //TODO add dependencies
            timer.Elapsed +=  async (a,e) => await OneTick();

        }

        Timer timer { get; } = new Timer(1000){Enabled  = false };

        async Task OneTick()
        {
            Generate();
            foreach (var item in LastIteration)
            {
                _context.Update(item);
            }         
            await _context.SaveChangesAsync();

        }


        // Get: Generator/CurrentGameState
        [HttpGet]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CurrentGameState()
        {
            var tmp = LastIteration; //cache the collection
            return View(tmp);

        }

        // POST: Generator/BuyShares
        [HttpPost("{userID}/{stockID}/{amount}/")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> BuyShares(int userID, int stockID, int amount)
        {
            var user = _context.Users.Find(userID);
            var stock = _context.Stock.Find(stockID);
            
            if(amount > 0 && user.userPortfolio.cash >= stock.firstValue * amount)
            {
                var portfolio = user.userPortfolio;
                user.userPortfolio.cash -= stock.firstValue * amount;

                if (user.userPortfolio.listOfShares.Any(x => x.id == stockID))
                    user.userPortfolio.listOfShares.Find(x => x.id == stockID).amount += amount;
                else
                    user.userPortfolio.listOfShares.Add(new Share()
                    {
                        stockId = stockID,
                        amount = amount,
                        portfolioId = user.userPortfolio.id
                    });

                await _context.SaveChangesAsync();
                transactions.AddOrUpdate(stockID, amount, (_, oldVal) => oldVal + amount);

                return Ok();
            }
            return BadRequest();

        }
        // POST: Generator/BuyShares
        [HttpPost("{userID}/{stockID}/{amount}/")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SellShares(int userID, int stockID, int amount)
        {
            var user = _context.Users.Find(userID);
            var stock = _context.Stock.Find(stockID);

            if (amount > 0 && user.userPortfolio.listOfShares.Find(x => x.id == stockID).amount >= amount)
            {
                var portfolio = user.userPortfolio;
                user.userPortfolio.cash += stock.firstValue * amount *(1-TaxRate);

                if (user.userPortfolio.listOfShares.Any(x => x.id == stockID))
                    user.userPortfolio.listOfShares.Find(x => x.id == stockID).amount += amount;
                else
                    user.userPortfolio.listOfShares.Add(new Share()
                    {
                        stockId = stockID,
                        amount = amount,
                        portfolioId = user.userPortfolio.id
                    });

                await _context.SaveChangesAsync();
                transactions.AddOrUpdate(stockID, -amount, (_, oldVal) => oldVal - amount);

                return Ok();
            }
            return BadRequest();

        }

        void Generate()
        {
            var trans = transactions;
            transactions = new ConcurrentDictionary<int, int>();
            for (int i = 0; i < IterationsPerTicks; i++)
            {
                var buffer = new List<Stock>();
                var deps = PropagateDependencies(LastIteration, Dependencies);
                foreach (var stock in LastIteration)
                {
                    var tmp = stock;

                    var modif = deps.GetValueOrDefault(tmp.id, 0.0) + trans.GetValueOrDefault(tmp.id, 0)/2;    // TODO smoothen
                    tmp = Generator.RandomlyModify(tmp, modif);
                    buffer.Add(tmp);
                }
                LastIteration = buffer;
                buffer = new List<Stock>();
                trans.Clear();
            }
        }


        StockValueInTime StockToTimeStock(Stock item)
        {
            return new StockValueInTime()
            {
                stockId = item.id,
                timestamp = Ticks,
                value = item.firstValue
            };
        }

        // both 'last' and 'dependencies' are expected to be (ascending)ordered by id 
        Dictionary<int,double> PropagateDependencies(List<Stock> last, List<StockDependency> dependencies)
        {
            int i = 0;
            var curr = last[i];
            var dict = new Dictionary<int, double>();
            foreach (var dependency in dependencies)
            {
                while (curr.id != dependency.SourceID)
                {
                    if (i != last.Count - 1)
                    {
                        var tmp = curr;
                        curr = last[++i];
                        if (tmp.id > curr.id)
                            throw new Exception("stock-list is not ordered!");
                    }
                    else break;
                }
                if (dict.TryGetValue(dependency.TargetID, out var value))
                {
                    dict[dependency.TargetID] = value + dependency.multiplier * curr.growTrend;
                }
                else         
                {
                    dict[dependency.TargetID] = dependency.multiplier * curr.growTrend;
                }
            }
            return dict;
        }

    }
}
