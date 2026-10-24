#pragma once
#include <cstdint>
#include <string_view>

namespace logging
{
    enum class LogL/*LogLevel*/ : uint8_t
    {
        Debug,
        Trace,
        Info,
        Warning,
        Error,
        Fatal
    };
    
    std::string_view getName(LogL level);
}
