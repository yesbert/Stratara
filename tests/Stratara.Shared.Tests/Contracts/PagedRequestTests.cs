using Stratara.Contracts.Requests;

namespace Stratara.Shared.Tests.Contracts;

public class PagedRequestTests
{
    [Fact]
    public void DefaultConstructor_SetsDefaultPage()
    {
        var request = new PagedRequest();

        Assert.Equal(1, request.Page);
    }

    [Fact]
    public void DefaultConstructor_SetsDefaultPageSize()
    {
        var request = new PagedRequest();

        Assert.Equal(100, request.PageSize);
    }

    [Fact]
    public void DefaultPage_Constant_IsOne()
    {
        Assert.Equal(1, PagedRequest.DefaultPage);
    }

    [Fact]
    public void DefaultPageSize_Constant_IsOneHundred()
    {
        Assert.Equal(100, PagedRequest.DefaultPageSize);
    }

    [Fact]
    public void Constructor_WithCustomValues_SetsValues()
    {
        var request = new PagedRequest(Page: 5, PageSize: 50);

        Assert.Equal(5, request.Page);
        Assert.Equal(50, request.PageSize);
    }

    [Fact]
    public void Constructor_WithNullValues_SetsNull()
    {
        var request = new PagedRequest(Page: null, PageSize: null);

        Assert.Null(request.Page);
        Assert.Null(request.PageSize);
    }
}
