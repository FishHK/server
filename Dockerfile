# ビルド環境 (.NET 10.0 に変更)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish -c Release -o /app

# 実行環境 (.NET 10.0 に変更)
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# Renderが指定するポート（10000番など）で動くように設定
ENV ASPNETCORE_URLS=http://+:10000
EXPOSE 10000
# プロジェクト名に合わせて server.dll に変更
ENTRYPOINT ["dotnet", "server.dll"]