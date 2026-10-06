#include "log_level.h"

#include <string_view>

namespace logging
{
    std::string_view getName(LogL level)
    {
        switch (level)
        {
        case LogL::Fatal:return "Fatal";
        case LogL::Error:return "Error";
        case LogL::Warning:return "Warning";
        case LogL::Info:return "Info";
        case LogL::Debug:return "Debug";
        }

        return "Unknown";
    }
}
