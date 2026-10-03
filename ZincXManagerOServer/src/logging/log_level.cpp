#include "log_level.h"

#include <string_view>

namespace logging
{
    std::string_view getName(LogLevel level)
    {
        switch (level)
        {
        case LogLevel::Fatal:return "Fatal";
        case LogLevel::Error:return "Error";
        case LogLevel::Warning:return "Warning";
        case LogLevel::Info:return "Info";
        case LogLevel::Debug:return "Debug";
        }

        return "Unknown";
    }
}
