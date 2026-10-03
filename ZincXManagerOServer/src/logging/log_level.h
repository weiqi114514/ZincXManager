#pragma once
#include <cstdint>
#include <string_view>

namespace logging
{
    enum class LogLevel : std::uint8_t
    {
        Debug,
        Info,
        Warning,
        Error,
        Fatal
    };
    
    std::string_view getName(LogLevel level);
}
