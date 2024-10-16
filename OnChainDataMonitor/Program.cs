using Nethereum.Hex.HexTypes;
using Nethereum.Web3;
using Nethereum.Contracts;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Net.Http;
using Newtonsoft.Json.Linq;
using System.Net;
using System.Web;

namespace OnChainDataMonitor
{
    internal class Program
    {
        static Dictionary<string, (string symbol, byte decimals)> erc20Token = new Dictionary<string, (string symbol, byte decimals)>();
        static Dictionary<string,decimal> erc20Price = new Dictionary<string, decimal>();
        static HexBigInteger blockNumber;
        static async Task Main(string[] args)
        {
            var jsonResult = await MakeAPICall();
            ParseJson(jsonResult);
            var web3 = new Web3("https://eth-mainnet.g.alchemy.com/v2/kKsfsmWqWAl6TxLCh-A-oyss8m4LMxxR");

            while(true)
            {
                var blocknum = await web3.Eth.Blocks.GetBlockNumber.SendRequestAsync();
                // 要查询的区块编号或哈希
                var blockNumberNew = new HexBigInteger(blocknum);
                if(blockNumberNew!= blockNumber)
                {
                    blockNumber = blockNumberNew;
                    await ScanBlock(web3, blockNumber);

                }
            }
        }

        static async Task ScanBlock(Web3 web3, HexBigInteger blockNumber)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();

            // 获取区块信息
            var blockWithTransactions = await web3.Eth.Blocks.GetBlockWithTransactionsByNumber.SendRequestAsync(blockNumber);

            // 打印区块中的交易数量
            Console.WriteLine($"区块 {blockWithTransactions.Number.Value} 包含 {blockWithTransactions.Transactions.Length} 笔交易");

            // 遍历交易列表并打印每笔交易的详细信息
            foreach (var tx in blockWithTransactions.Transactions)
            {
                if (Web3.Convert.FromWei(tx.Value) > 400)
                {
                    Console.WriteLine($"交易哈希: {tx.TransactionHash},发送方: {tx.From},接收方: {tx.To},交易金额 (ETH): {Web3.Convert.FromWei(tx.Value)}");
                }
                if (tx.Input.StartsWith("0xa9059cbb"))
                {
                    var tokenAddress = tx.To; // 代币合约地址
                    var tokenTo = "0x" + tx.Input.Substring(34, 40);   //ERC20 Token 接收地址
                    var amount = new HexBigInteger(tx.Input.Substring(74, 64)); // 获取转移数量

                    var symbol = "";
                    if (!erc20Token.ContainsKey(tokenAddress))
                    {
                        Contract contract;
                        var erc20Abi = @"[{ ""constant"": true, ""inputs"": [], ""name"": ""symbol"", ""outputs"": [{ ""name"": """", ""type"": ""string"" }], ""payable"": false, ""stateMutability"": ""view"", ""type"": ""function"" },
                                    {""constant"":true,""inputs"":[],""name"":""decimals"",""outputs"":[{""name"":"""",""type"":""uint8""}],""payable"":false,""stateMutability"":""view"",""type"":""function""}]";
                        contract = web3.Eth.GetContract(erc20Abi, tokenAddress);
                        try
                        {
                            var symbolFunction = contract.GetFunction("symbol");
                            symbol = await symbolFunction.CallAsync<string>();
                        }
                        catch
                        {
                            symbol = "";
                        }
                        if (symbol == "")
                        {
                            try
                            {
                                erc20Abi = @"[{ ""constant"": true, ""inputs"": [], ""name"": ""symbol"", ""outputs"": [{ ""name"": """", ""type"": ""bytes32"" }], ""payable"": false, ""stateMutability"": ""view"", ""type"": ""function"" },
                                    {""constant"":true,""inputs"":[],""name"":""decimals"",""outputs"":[{""name"":"""",""type"":""uint8""}],""payable"":false,""stateMutability"":""view"",""type"":""function""}]";
                                contract = web3.Eth.GetContract(erc20Abi, tokenAddress);
                                var symbolFunction = contract.GetFunction("symbol");
                                symbol = await symbolFunction.CallAsync<string>();
                            }
                            catch
                            {
                                symbol = "";
                            }
                        }
                        if (symbol == "")
                        {
                            symbol = "ERROR";
                            erc20Token[tokenAddress] = (symbol, 18);

                        }
                        else
                        {
                            try
                            {
                                var decimalsFunction = contract.GetFunction("decimals");
                                var decimals = await decimalsFunction.CallAsync<byte>();
                                erc20Token[tokenAddress] = (symbol, decimals);
                            }
                            catch
                            {
                                erc20Token[tokenAddress] = (symbol, 0);

                            }

                        }



                    }
                    if(erc20Price.ContainsKey(symbol))
                    {
                        var amountWithDecimals = Web3.Convert.FromWei(amount, erc20Token[tokenAddress].decimals);
                        var amountInUSD = erc20Price[symbol] * amountWithDecimals;
                        if (amountInUSD > 1000000)
                        {
                            Console.WriteLine($"交易哈希: {tx.TransactionHash},发送方: {tx.From},接收方: {tokenTo},交易量 ({erc20Token[tokenAddress].symbol}): {amountWithDecimals},交易额:{amountInUSD}");

                        }
                    }
                }
            }
            double t = stopwatch.ElapsedMilliseconds / 1000.0;
            Console.WriteLine($"区块 {blockWithTransactions.Number.Value} 扫描结束,耗时 {t} 秒");

        }

        public static void WriteCsv(string filename, string[] data)
        {
            StreamWriter fileWriter = new StreamWriter(filename, true, Encoding.Default);
            fileWriter.WriteLine(String.Join(",", data));
            fileWriter.Flush();
            fileWriter.Close();
        }

        public static string[] ReadCsv(string filename)
        {
            StreamReader fileReader = new StreamReader(filename, Encoding.Default);
            string s_data = fileReader.ReadToEnd();
            fileReader.Close();
            string[] data = Regex.Split(s_data, "\r\n");
            return data;
        }

        private static string API_KEY = "c3189f52-161e-4781-8d67-287bdff425df";
        static async Task<string> MakeAPICall()
        {
            var url = "https://pro-api.coinmarketcap.com/v1/cryptocurrency/listings/latest?start=1&limit=5000&convert=USD&sort_dir=desc";
            using (HttpClient client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("X-CMC_PRO_API_KEY", API_KEY);
                client.DefaultRequestHeaders.Add("Accepts", "application/json");

                try
                {
                    HttpResponseMessage response = await client.GetAsync(url);
                    response.EnsureSuccessStatusCode(); // 确保请求成功
                    string responseBody = await response.Content.ReadAsStringAsync();
                    return responseBody; // 返回响应字符串
                }
                catch (HttpRequestException e)
                {
                    Console.WriteLine($"CoinMarketCap 请求出错: {e.Message}");
                    return null;
                }
            }
        }

        // 解析返回的 JSON 数据
        static void ParseJson(string jsonResult)
        {
            if (string.IsNullOrEmpty(jsonResult))
            {
                Console.WriteLine("CoinMarketCap 返回结果为空");
                return;
            }

            try
            {
                // 使用 Newtonsoft.Json 解析 JSON
                JObject json = JObject.Parse(jsonResult);

                // 提取代币列表
                var data = json["data"];
                foreach (var token in data)
                {
                    string name = token["name"].ToString();
                    string symbol = token["symbol"].ToString();
                    decimal price = token["quote"]["USD"]["price"].Value<decimal>();
                    erc20Price[symbol] = price;
                    //Console.WriteLine($"名称: {name}, 符号: {symbol}, 价格: {price} USD");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"解析 JSON 时出错: {ex.Message}");
            }
        }
    }
}
