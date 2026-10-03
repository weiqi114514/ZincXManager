#include "logger.h"

#include <chrono>
#include <iostream>

namespace logging
{
    void Logger::log(LogLevel level, std::string_view message)
    {
        auto now = std::chrono::system_clock::now();
        auto time = std::format("{:%Y-%m-%d %H:%M:%S}",now);
        std::string line;
        line += "[";
        line += getName(level);
        line += "] [";
        line += time;
        line += "] ";
        line += message;
        line += "\n";
        std::cout << line;
    }
}

