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
using System.Threading;
using Nethereum.RPC.Eth.DTOs;
using static System.Net.Mime.MediaTypeNames;
using System.Runtime.InteropServices;

namespace OnChainDataMonitor
{
    internal class Program
    {
        public struct transactionData
        {
            public string dateTime;
            public string blockNumber;
            public string transactionHash;
            public string from;
            public string to;
            public string tokenSymbol;
            public string tokenAmount;
            public string tokenAmountInUSD;
        }
        static Dictionary<string, (string symbol, byte decimals)> erc20Token = new Dictionary<string, (string symbol, byte decimals)>();
        static Dictionary<string,decimal> erc20Price = new Dictionary<string, decimal>();
        static HexBigInteger blockNumber;
        static async Task Main(string[] args)
        {
            blockNumber = new HexBigInteger(Convert.ToInt64(Helper.ReadIniData("Config", "blocknumber", "", @"Config//Config.ini")));
            var jsonResult = await MakeAPICall();
            ParseJson(jsonResult);
            var web3 = new Web3("https://eth-mainnet.g.alchemy.com/v2/kKsfsmWqWAl6TxLCh-A-oyss8m4LMxxR");
            var blocknum = await web3.Eth.Blocks.GetBlockNumber.SendRequestAsync();
            // 要查询的区块编号或哈希
            var blockNumberNew = new HexBigInteger(blocknum);
            while (true)
            {
                Thread.Sleep(10);
                if(blockNumberNew.Value> blockNumber.Value)
                {
                    await ScanBlock(web3, blockNumber);
                    blockNumber.Value = blockNumber.Value + 1;
                    Helper.WriteIniData("Config", "blocknumber", blockNumber.Value.ToString(), @"Config//Config.ini");
                }
            }
        }

        static async Task ScanBlock(Web3 web3, HexBigInteger blockNumber)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();

            // 获取区块信息
            var blockWithTransactions = await web3.Eth.Blocks.GetBlockWithTransactionsByNumber.SendRequestAsync(blockNumber);

            // 打印区块中的交易数量
            Console.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss:fff ")+$"区块 {blockWithTransactions.Number.Value} 包含 {blockWithTransactions.Transactions.Length} 笔交易");

            // 遍历交易列表并打印每笔交易的详细信息
            foreach (var tx in blockWithTransactions.Transactions)
            {
                if (Web3.Convert.FromWei(tx.Value) > 2000)
                {
                    transactionData txData = new transactionData()
                    {
                        dateTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss:fff"),
                        blockNumber = blockWithTransactions.Number.Value.ToString(),
                        transactionHash = tx.TransactionHash,
                        from = tx.From,
                        to = tx.To,
                        tokenSymbol = "ETH",
                        tokenAmount = Web3.Convert.FromWei(tx.Value).ToString("F3"),
                        tokenAmountInUSD = (erc20Price["ETH"] * Web3.Convert.FromWei(tx.Value) / 1000000).ToString("F3")
                    };
                    WriteLog(txData);
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
                        if (symbol == ""|| symbol==null)
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
                        if (symbol == ""||symbol == null)
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
                    else
                    {
                        symbol = erc20Token[tokenAddress].symbol;
                    }
                    if(erc20Price.ContainsKey(symbol))
                    {
                        var amountWithDecimals = Web3.Convert.FromWei(amount, erc20Token[tokenAddress].decimals);
                        var amountInUSD = erc20Price[symbol] * amountWithDecimals;
                        if (amountInUSD > 5000000)
                        {
                            transactionData txData = new transactionData()
                            {
                                dateTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss:fff"),
                                blockNumber = blockWithTransactions.Number.Value.ToString(),
                                transactionHash = tx.TransactionHash,
                                from = tx.From,
                                to = tokenTo,
                                tokenSymbol = erc20Token[tokenAddress].symbol,
                                tokenAmount = amountWithDecimals.ToString("F3"),
                                tokenAmountInUSD = (amountInUSD / 1000000).ToString("F3")
                            };
                            WriteLog(txData);
                        }
                    }
                }
            }
            double t = stopwatch.ElapsedMilliseconds / 1000.0;
            Console.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss:fff ") + $"区块 {blockWithTransactions.Number.Value} 扫描结束,耗时 {t} 秒");
            Console.WriteLine("----------------------------------------------------------------------------------------------------");
        }

        private static string API_KEY = "c3189f52-161e-4781-8d67-287bdff425df";
        static async Task<string> MakeAPICall()
        {
            Console.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss:fff ") + $"从CoinMarketCap获取价格......");
            var url = "https://pro-api.coinmarketcap.com/v1/cryptocurrency/listings/latest?start=1&limit=2000&convert=USD&sort_dir=desc";
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
                    //string tokenAddress = token["platform"]["token_address"].ToString();
                    erc20Price[symbol] = price;
                    //Console.WriteLine($"名称: {name}, 符号: {symbol}, 价格: {price} USD");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"解析 JSON 时出错: {ex.Message}");
            }
        }

        public static void WriteLog(transactionData txData)
        {
            Console.WriteLine($"{txData.dateTime}  区块: {txData.blockNumber}, 交易哈希: {txData.transactionHash}, 发送方: {txData.from}, 接收方: {txData.to}, 交易量({txData.tokenSymbol}): {txData.tokenAmount}, 交易额:{txData.tokenAmountInUSD} M");
            Helper.WriteCsv(@"Log//" + DateTime.Now.ToString("yyyy-MM-dd") + ".csv", new string[] {txData.dateTime,txData.blockNumber,txData.transactionHash,txData.from,txData.to,txData.tokenSymbol,txData.tokenAmount,txData.tokenAmountInUSD });
        }

    }

    public static class Helper
    {
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

        #region API函数声明

        [DllImport("kernel32")]//返回0表示失败，非0为成功
        private static extern long WritePrivateProfileString(string section, string key,
            string val, string filePath);

        [DllImport("kernel32")]//返回取得字符串缓冲区的长度
        private static extern long GetPrivateProfileString(string section, string key,
            string def, StringBuilder retVal, int size, string filePath);

        #endregion

        #region 读Ini文件

        public static string ReadIniData(string Section, string Key, string NoText, string iniFilePath)
        {
            if (File.Exists(iniFilePath))
            {
                StringBuilder temp = new StringBuilder(1024);
                GetPrivateProfileString(Section, Key, NoText, temp, 1024, iniFilePath);
                return temp.ToString();
            }
            else
            {
                return String.Empty;
            }
        }

        #endregion

        #region 写Ini文件

        public static bool WriteIniData(string Section, string Key, string Value, string iniFilePath)
        {
            if (File.Exists(iniFilePath))
            {
                long OpStation = WritePrivateProfileString(Section, Key, Value, iniFilePath);
                if (OpStation == 0)
                {
                    return false;
                }
                else
                {
                    return true;
                }
            }
            else
            {
                return false;
            }
        }

        #endregion
    }
}
