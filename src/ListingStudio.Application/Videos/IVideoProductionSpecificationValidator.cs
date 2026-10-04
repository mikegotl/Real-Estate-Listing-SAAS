using ListingStudio.Domain.Videos;

namespace ListingStudio.Application.Videos;

public interface IVideoProductionSpecificationValidator
{
    VideoSpecificationValidationResult Validate(
        VideoDirectionRequest authoritativeInput,
        VideoProductionSpecification specification);
}
