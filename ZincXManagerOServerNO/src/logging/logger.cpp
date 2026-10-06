#include "logger.h"        
#include <chrono>
#include <iostream>
#include <format>           // C++20 std::format
#include "../../ZFile.h"    // 为了使用 zFile::logF

namespace logging
{
    void log(LogL level, std::string_view message)
    {
        auto now = std::chrono::system_clock::now();
        auto sec = std::chrono::floor<std::chrono::seconds>(now);
        auto time = std::format("{:%Y-%m-%d %H:%M:%S}", sec);

        std::string line;
        line += "[";
        line += getName(level);
        line += "] [";
        line += time;
        line += "] ";
        line += message;
        line += "\n";

        std::cout << line;

        // 写入文件
        zFile::logF.writeFileTxt(line,0);
    }
}