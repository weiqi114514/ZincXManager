#include <iostream>
#include <format>
#include <chrono>
#include <string>
#include "ZFile.h"
#include "src/logging/logger.h"
#include <future>
#include <windows.h>
static void InitializeLog()//初始化函数
{
    auto now = std::chrono::system_clock::now();
    auto sec = std::chrono::floor<std::chrono::seconds>(now);

    std::string fileName = std::format("ZXMS{:%Y-%m-%d_%H-%M-%S}.log", sec);

    if (!zFile::logF.fileOperate(fileName, "rwNFC"))
    {
        std::cerr << "打开日志文件失败: " << fileName << "\n";
    }
}
void ConsoleColor()//设置控制台输出颜色函数
{
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    SetConsoleTextAttribute(hOut, FOREGROUND_GREEN | FOREGROUND_INTENSITY);
}
int main()
{
    ConsoleColor();//设置控制台输出颜色
    InitializeLog();//初始化
    //以下为测试代码
    zFile::ZFile fileo;
    auto asyncResult = std::async(std::launch::async, []() {
        logging::log(logging::LogL::Info, "测试2");
        logging::log(logging::LogL::Warning, "测试3");
        logging::log(logging::LogL::Fatal, "测试4");
        });
    logging::log(logging::LogL::Fatal, "测试1");
    fileo.fileOperate("config.ini", "rwNFC");
    fileo.writeFileMap("name", "weiqi");
    std::cout<<"测试读取MAP " << fileo.mapGet("name") << std::endl;
    fileo.closeFile();
    logging::log(logging::LogL::Debug, "测试5");
    
    int a;
    std::cin >> a;
    return 0;
}