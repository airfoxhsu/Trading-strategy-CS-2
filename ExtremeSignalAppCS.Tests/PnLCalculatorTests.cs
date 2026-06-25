using System;
using Xunit;
using ExtremeSignalAppCS.Services;

namespace ExtremeSignalAppCS.Tests
{
    public class PnLCalculatorTests
    {
        [Fact]
        public void Test_BuyThenSell_TX_FullClose()
        {
            // Arrange
            var calculator = new PnLCalculator();

            // Act
            // 大臺買進 1 口 @ 20000
            calculator.AddExecution("TX06", "B", 20000, 1);
            // 大臺賣出 1 口 @ 20100 (平倉)
            calculator.AddExecution("TX06", "S", 20100, 1);

            // Assert
            // 預期損益 = (20100 - 20000) * 200 * 1 = 20000
            Assert.Equal(20000.0, calculator.TotalPnL);
        }

        [Fact]
        public void Test_SellThenBuy_MTX_FullClose()
        {
            // Arrange
            var calculator = new PnLCalculator();

            // Act
            // 小臺放空 2 口 @ 20000
            calculator.AddExecution("MTX06", "S", 20000, 2);
            // 小臺回補 2 口 @ 19900 (平倉)
            calculator.AddExecution("MTX06", "B", 19900, 2);

            // Assert
            // 預期損益 = (20000 - 19900) * 50 * 2 = 10000
            Assert.Equal(10000.0, calculator.TotalPnL);
        }

        [Fact]
        public void Test_FIFO_Ordering()
        {
            // Arrange
            var calculator = new PnLCalculator();

            // Act
            // 1. 買進第一口 @ 20000
            calculator.AddExecution("TX06", "B", 20000, 1);
            // 2. 買進第二口 @ 20100
            calculator.AddExecution("TX06", "B", 20100, 1);
            // 3. 賣出第一口 @ 20200 (應沖銷第一口 20000)
            calculator.AddExecution("TX06", "S", 20200, 1); // 損益 += (20200 - 20000) * 200 = 40000
            
            double pnlAfterFirstClose = calculator.TotalPnL;

            // 4. 賣出第二口 @ 20200 (應沖銷第二口 20100)
            calculator.AddExecution("TX06", "S", 20200, 1); // 損益 += (20200 - 20100) * 200 = 20000

            // Assert
            Assert.Equal(40000.0, pnlAfterFirstClose);
            Assert.Equal(60000.0, calculator.TotalPnL);
        }

        [Fact]
        public void Test_PartialClose()
        {
            // Arrange
            var calculator = new PnLCalculator();

            // Act
            // 買進 2 口 @ 20000
            calculator.AddExecution("TX06", "B", 20000, 2);
            // 賣出 1 口 @ 20050 (平倉 1 口) -> 損益 += (20050 - 20000) * 200 = 10000
            calculator.AddExecution("TX06", "S", 20050, 1);
            double pnl1 = calculator.TotalPnL;

            // 再賣出 1 口 @ 20100 (平倉最後 1 口) -> 損益 += (20100 - 20000) * 200 = 20000
            calculator.AddExecution("TX06", "S", 20100, 1);

            // Assert
            Assert.Equal(10000.0, pnl1);
            Assert.Equal(30000.0, calculator.TotalPnL);
        }

        [Fact]
        public void Test_Mixed_TX_MTX()
        {
            // Arrange
            var calculator = new PnLCalculator();

            // Act
            // 大臺買進 1 口 @ 20000
            calculator.AddExecution("TX06", "B", 20000, 1);
            // 小臺買進 2 口 @ 20000
            calculator.AddExecution("MTX06", "B", 20000, 2);

            // 大臺平倉 @ 20050 -> 損益 += 50 * 200 * 1 = 10000
            calculator.AddExecution("TX06", "S", 20050, 1);
            // 小臺平倉 @ 20020 -> 損益 += 20 * 50 * 2 = 2000
            calculator.AddExecution("MTX06", "S", 20020, 2);

            // Assert
            Assert.Equal(12000.0, calculator.TotalPnL);
        }

        [Fact]
        public void Test_Direction_Normalization()
        {
            // Arrange
            var calculator = new PnLCalculator();

            // Act & Assert
            // 測試中文與代碼
            calculator.AddExecution("TX06", "買進", 20000, 1);
            calculator.AddExecution("TX06", "賣出", 20100, 1);
            Assert.Equal(20000.0, calculator.TotalPnL);

            calculator.Reset();
            calculator.AddExecution("TX06", "1", 20000, 1); // "1" 代表買
            calculator.AddExecution("TX06", "2", 20100, 1); // "2" 代表賣
            Assert.Equal(20000.0, calculator.TotalPnL);
        }
    }
}
