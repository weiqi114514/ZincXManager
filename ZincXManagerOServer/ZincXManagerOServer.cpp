#include <iostream>
#include <format>
#include <chrono>
#include <string>
#include "ZFile.h"
#include "src/logging/logger.h"
static void initializeLog()
{
    auto now = std::chrono::system_clock::now();
    auto sec = std::chrono::floor<std::chrono::seconds>(now);

    // 文件名不能带 ':'，改成 '-'；模式字符串大小写要和 ZFile.cpp 里完全一致
    std::string fileName = std::format("{:%Y-%m-%d_%H-%M-%S}.log", sec);

    if (!zFile::logF.fileOperate(fileName, "rwNfC"))
    {
        std::cerr << "打开日志文件失败: " << fileName << "\n";
    }
}

int main()
{
    initializeLog();
    //以下为测试代码
    logging::log(logging::LogL::Info, "测试1");
    logging::log(logging::LogL::Warning, "测试2");
    logging::log(logging::LogL::Fatal, "测试3");
    int a;
    std::cin >> a;
    return 0;
}